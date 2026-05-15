using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

// Admin-side invitation lifecycle commands: create channel, drain inbox,
// promote responded invitations to TrustedContacts, plus lifecycle cleanup
// (revoke, expire). Shared helpers + ctor live in ContactInvitationService.cs.
internal partial class ContactInvitationService
{
    /// <summary>
    /// Admin-side: create an invitation channel for a new contact. Generates
    /// a 32-byte transport secret, derives the transport keypair, builds a
    /// <see cref="ShareGroup"/> with admin + transport pubkey as members, and
    /// inserts an <see cref="Invitation"/> row that rides that group's CEK.
    /// Returns the <see cref="InvitationBundle"/> the admin ships out-of-band.
    ///
    /// <para>
    /// The transport secret IS the invitee's X25519 private key for the
    /// duration of the bootstrap channel — that's intrinsic to OOB delivery.
    /// On the wire the row's contents are opaque to anyone outside the
    /// invitation share group.
    /// </para>
    /// </summary>
    public async ValueTask<InvitationBundle> CreateInvitationAsync(
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        string username,
        string? email = null,
        string? comment = null,
        string? relayHint = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentSaltBase64);

        var now = DateTime.UtcNow;
        var expiresAt = now + (ttl ?? DefaultInvitationTtl);
        var groupId = Guid.NewGuid();
        var groupContext = $"invitation-{groupId:N}:v1";

        // Generate transport secret + derive both transport keypairs (X25519
        // for ECDH, Ed25519 for relay POST auth via the whitelist).
        var transportSecret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var transportDual = await crypto.DeriveDualKeyPairAsync(transportSecret).ConfigureAwait(false);
        string transportPub;
        string transportEd25519Pub;
        try
        {
            transportPub = transportDual.X25519PublicKey;
            transportEd25519Pub = transportDual.Ed25519PublicKey;
        }
        finally
        {
            transportDual.Clear();
        }

        // Create the invitation share group with admin + transportPub as members.
        var adminPriv = adminKeys.X25519PrivateKey;
        IReadOnlyList<WrappedKey> wrappedKeys;
        int keyVersion;
        try
        {
            var bundleResult = await groupEncryption.CreateGroupKeysAsync(
                adminPriv, adminKeys.X25519PublicKey,
                [adminKeys.X25519PublicKey, transportPub],
                groupContext);
            if (!bundleResult.Success)
            {
                throw new InvalidOperationException(
                    $"ContactInvitationService.CreateInvitationAsync: CreateGroupKeysAsync failed: {bundleResult.ErrorCode}");
            }
            var bundle = bundleResult.Value
                ?? throw new InvalidOperationException(
                    "ContactInvitationService.CreateInvitationAsync: CreateGroupKeysAsync returned null bundle");
            wrappedKeys = bundle.MemberKeys;
            keyVersion = bundle.KeyVersion;
        }
        finally
        {
        }

        var adminWrapped = wrappedKeys.Single(k => k.MemberPublicKey == adminKeys.X25519PublicKey);
        var transportWrapped = wrappedKeys.Single(k => k.MemberPublicKey == transportPub);

        // Sign ShareTarget credentials with admin's Ed25519 key.
        var adminEd25519Priv = adminKeys.Ed25519PrivateKey;
        byte[] adminTargetSig;
        byte[] transportTargetSig;
        byte[] bundleSignatureBytes;
        try
        {
            adminTargetSig = await signer.SignShareTargetAsync(
                adminEd25519Priv, adminKeys.X25519PublicKey, SyncRole.OWNER,
                groupContext, keyVersion).ConfigureAwait(false);
            transportTargetSig = await signer.SignShareTargetAsync(
                adminEd25519Priv, transportPub, SyncRole.OWNER,
                groupContext, keyVersion).ConfigureAwait(false);

            // Sign bundle canonical: transportPub || GroupId.ToByteArray() || ExpiresAt.Ticks.
            var canonicalBase64 = BuildBundleCanonical(transportPub, groupId, expiresAt);
            var sigResult = await crypto.SignAsync(canonicalBase64, adminEd25519Priv).ConfigureAwait(false);
            if (!sigResult.Success || sigResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"ContactInvitationService.CreateInvitationAsync: SignAsync failed: {sigResult.ErrorCode}");
            }
            bundleSignatureBytes = Convert.FromBase64String(sigResult.Value);
        }
        finally
        {
        }

        // Look up admin's contact id (for ShareTarget.GrantedByContactId).
        var adminContact = await context.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsAdmin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "ContactInvitationService.CreateInvitationAsync: no admin TrustedContact found in local DB.");

        // Whitelist the transport Ed25519 pubkey before committing the local
        // invitation rows. If the relay push fails, the method fails closed
        // with no local bootstrap channel that the relay would reject.
        var transportHash = WhitelistPushService.HashPubkey(deploymentSaltBase64, transportEd25519Pub);
        await PushWhitelistOpsAsync(
            adminKeys,
            [WhitelistOp.Add(transportHash)],
            cancellationToken).ConfigureAwait(false);

        // Persist ShareGroup + 2 ShareTargets + Invitation row in one transaction.
        await using var tx = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        context.ShareGroups.Add(new ShareGroup
        {
            Id = groupId,
            GroupContext = groupContext,
            KeyVersion = keyVersion,
            GroupAdminPublicKey = adminKeys.X25519PublicKey,
            CreatedAt = now,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        });

        context.ShareTargets.Add(new ShareTarget
        {
            Id = Guid.NewGuid(),
            ShareGroupId = groupId,
            KeyVersion = keyVersion,
            MemberPublicKey = adminKeys.X25519PublicKey,
            WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(adminWrapped.WrappedContentKey),
            Role = SyncRole.OWNER,
            AdminSignature = adminTargetSig,
            GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
            GrantedByContactId = adminContact.Id,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        });

        context.ShareTargets.Add(new ShareTarget
        {
            Id = Guid.NewGuid(),
            ShareGroupId = groupId,
            KeyVersion = keyVersion,
            MemberPublicKey = transportPub,
            WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(transportWrapped.WrappedContentKey),
            Role = SyncRole.OWNER,
            AdminSignature = transportTargetSig,
            GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
            GrantedByContactId = adminContact.Id,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        });

        // Reuse the invitation share group's Id as the Invitation row Id —
        // simplifies the invitee's response signature (no need to pull the
        // row first to learn its Id) and ties the row 1:1 to the channel.
        context.Invitations.Add(new Invitation
        {
            Id = groupId,
            Username = username,
            Email = email,
            Comment = comment,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            UpdatedAt = now,
            SharingScope = SharingScope.SHARED,
            SharingId = groupContext,
            TransportEd25519PublicKey = transportEd25519Pub,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new InvitationBundle
        {
            TransportSecret = transportSecret,
            GroupId = groupId,
            ExpiresAt = expiresAt,
            AdminSignature = bundleSignatureBytes,
            AdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
            AdminX25519PublicKey = adminKeys.X25519PublicKey,
            RelayHint = relayHint
        };
    }

    /// <summary>
    /// Hard-delete invitations whose <see cref="Invitation.ExpiresAt"/> is in
    /// the past. Cleans up the invitation share group + ShareTargets and
    /// pushes a single batched <c>WhitelistOp.Revoke</c> covering every
    /// expired transport Ed25519 key so the relay stops accepting POSTs from
    /// the dead channels.
    /// </summary>
    public async ValueTask DeleteExpiredInvitationsAsync(
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentSaltBase64);

        var now = DateTime.UtcNow;
        var expired = await context.Invitations
            .Where(i => i.ExpiresAt <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return;
        }

        // Push first: if the relay rejects the batch, the local rows remain
        // and the caller can retry. Deleting first would orphan the relay
        // whitelist if the push later fails. Rows missing TransportEd25519PublicKey
        // were never whitelisted, so they contribute no revoke op.
        var revokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var revokeOps = new List<WhitelistOp>(expired.Count);
        foreach (var i in expired)
        {
            if (i.TransportEd25519PublicKey is { } transportKey)
            {
                revokeOps.Add(WhitelistOp.Revoke(
                    WhitelistPushService.HashPubkey(deploymentSaltBase64, transportKey),
                    revokedAt));
            }
        }
        if (revokeOps.Count > 0)
        {
            await PushWhitelistOpsAsync(adminKeys, revokeOps, cancellationToken).ConfigureAwait(false);
        }

        foreach (var invitation in expired)
        {
            await DeleteInvitationChannelAsync(invitation, cancellationToken).ConfigureAwait(false);
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hard-delete a single invitation by id (admin revoke). Pushes
    /// <c>WhitelistOp.Revoke</c> for the invitation's transport Ed25519 key
    /// first, then removes the invitation share group + both ShareTargets +
    /// the Invitation row. Pushing first ensures the relay stops accepting
    /// POSTs through this channel even if the local cleanup later fails.
    /// </summary>
    public async ValueTask RevokeInvitationAsync(
        Guid invitationId,
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentSaltBase64);

        var invitation = await context.Invitations
            .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"ContactInvitationService.RevokeInvitationAsync: invitation {invitationId} not found.");

        if (invitation.TransportEd25519PublicKey is { } transportKey)
        {
            var transportHash = WhitelistPushService.HashPubkey(deploymentSaltBase64, transportKey);
            var revokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await PushWhitelistOpsAsync(
                adminKeys,
                [WhitelistOp.Revoke(transportHash, revokedAt)],
                cancellationToken).ConfigureAwait(false);
        }

        await DeleteInvitationChannelAsync(invitation, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Admin-side: drain pending <see cref="InvitationResponseEnvelope"/>
    /// envelopes from <paramref name="syncTransport"/>, decrypt each via
    /// <c>HKDF(ECDH(adminPriv, transportPub), info=invitationGroupContext)</c>,
    /// verify the contact signature, and update the local
    /// <see cref="Invitation"/> row with the contact's pubkeys + self-group
    /// material. Returns the count of rows updated.
    /// </summary>
    public async ValueTask<int> IngestInvitationResponsesAsync(
        DualKeyPairFull adminKeys,
        ISyncTransport syncTransport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminKeys);
        ArgumentNullException.ThrowIfNull(syncTransport);

        var updated = 0;
        while (true)
        {
            var wireBytes = await syncTransport.TryReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (wireBytes is null)
            {
                break;
            }

            InvitationResponseEnvelope envelope;
            try
            {
                envelope = MessagePackSerializer.Deserialize<InvitationResponseEnvelope>(wireBytes);
            }
            catch (MessagePackSerializationException)
            {
                // Not an invitation envelope — skip silently. Other transport
                // consumers may share the same inbox.
                continue;
            }

            var invitation = await context.Invitations
                .FirstOrDefaultAsync(i => i.Id == envelope.GroupId, cancellationToken)
                .ConfigureAwait(false);
            if (invitation is null)
            {
                // Stale envelope, no matching pending invitation — drop.
                continue;
            }

            var groupContext = invitation.SharingId;
            var transportTarget = await context.ShareTargets
                .AsNoTracking()
                .Where(t => t.ShareGroupId == envelope.GroupId
                    && t.MemberPublicKey != adminKeys.X25519PublicKey)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidInvitationResponseException(
                    $"IngestInvitationResponsesAsync: transport ShareTarget for invitation {envelope.GroupId} not found.");
            var transportPub = transportTarget.MemberPublicKey;

            var adminPriv = adminKeys.X25519PrivateKey;
            string plaintextBase64;
            try
            {
                var wkResult = await crypto.DeriveWrappingKeyAsync(adminPriv, transportPub, groupContext)
                    .ConfigureAwait(false);
                if (!wkResult.Success)
                {
                    throw new InvalidInvitationResponseException(
                        $"IngestInvitationResponsesAsync: DeriveWrappingKeyAsync failed: {wkResult.ErrorCode}");
                }
                try
                {
                    var decResult = await crypto.DecryptSymmetricAsync(
                        new SymmetricEncryptedData(
                            Convert.ToBase64String(envelope.Ciphertext),
                            Convert.ToBase64String(envelope.Nonce)),
                        wkResult.Value).ConfigureAwait(false);
                    if (!decResult.Success || decResult.Value is null)
                    {
                        throw new InvalidInvitationResponseException(
                            $"IngestInvitationResponsesAsync: DecryptSymmetricAsync failed: {decResult.ErrorCode}");
                    }
                    plaintextBase64 = decResult.Value;
                }
                finally
                {
                    ClearWrappingKey(wkResult.Value);
                }
            }
            finally
            {
            }

            var payload = MessagePackSerializer.Deserialize<InvitationResponsePayload>(
                Convert.FromBase64String(plaintextBase64));

            // Verify ContactSignature against the contact's claimed Ed25519
            // pubkey before persisting.
            var canonical = BuildContactSignatureCanonical(
                invitation.Id,
                payload.ContactX25519PublicKey,
                payload.ContactEd25519PublicKey,
                invitation.ExpiresAt);
            var sigOk = await crypto.VerifyAsync(
                canonical,
                Convert.ToBase64String(payload.ContactSignature),
                payload.ContactEd25519PublicKey).ConfigureAwait(false);
            if (!sigOk)
            {
                throw new InvalidInvitationResponseException(
                    $"IngestInvitationResponsesAsync: ContactSignature failed Ed25519 verification for invitation {envelope.GroupId}.");
            }

            invitation.ContactX25519PublicKey = payload.ContactX25519PublicKey;
            invitation.ContactEd25519PublicKey = payload.ContactEd25519PublicKey;
            invitation.ContactSignature = payload.ContactSignature;
            invitation.SelfGroupId = payload.SelfGroupId;
            invitation.SelfGroupContext = payload.SelfGroupContext;
            invitation.SelfKeyVersion = payload.SelfKeyVersion;
            invitation.SelfWrappedContentKey = payload.SelfWrappedContentKey;
            invitation.SelfShareTargetSignature = payload.SelfShareTargetSignature;
            invitation.UpdatedAt = DateTime.UtcNow;

            updated++;
        }

        if (updated > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return updated;
    }

    /// <summary>
    /// Admin-side: promote a responded <see cref="Invitation"/> row to a
    /// real <see cref="TrustedContact"/>. Verifies <see cref="Invitation.ContactSignature"/>,
    /// inserts the TrustedContact row + the contact's self-group ShareGroup
    /// + ShareTarget (admin can't unwrap — privacy invariant), wraps the
    /// admin's system CEK for the new contact, hard-deletes the invitation
    /// channel rows. Atomic — either all changes commit or none.
    /// </summary>
    public async ValueTask<TrustedContact> PromoteInvitationAsync(
        Guid invitationId,
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        SyncRole systemRole = SyncRole.EDITOR,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentSaltBase64);

        var invitation = await context.Invitations
            .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvitationNotFoundException(
                $"PromoteInvitationAsync: invitation {invitationId} not found.");

        if (DateTime.UtcNow >= invitation.ExpiresAt)
        {
            throw new InvitationExpiredException(
                $"PromoteInvitationAsync: invitation {invitationId} expired at {invitation.ExpiresAt:O}.");
        }

        if (invitation.ContactX25519PublicKey is null
            || invitation.ContactEd25519PublicKey is null
            || invitation.ContactSignature is null
            || invitation.SelfGroupId is null
            || invitation.SelfGroupContext is null
            || invitation.SelfKeyVersion is null
            || invitation.SelfWrappedContentKey is null
            || invitation.SelfShareTargetSignature is null)
        {
            throw new InvitationNotRespondedException(
                $"PromoteInvitationAsync: invitation {invitationId} has not been responded to yet.");
        }

        // Re-verify ContactSignature in case the row was tampered with after ingest.
        var canonical = BuildContactSignatureCanonical(
            invitation.Id,
            invitation.ContactX25519PublicKey,
            invitation.ContactEd25519PublicKey,
            invitation.ExpiresAt);
        var sigOk = await crypto.VerifyAsync(
            canonical,
            Convert.ToBase64String(invitation.ContactSignature),
            invitation.ContactEd25519PublicKey).ConfigureAwait(false);
        if (!sigOk)
        {
            throw new InvalidInvitationResponseException(
                $"PromoteInvitationAsync: ContactSignature failed Ed25519 verification for invitation {invitationId}.");
        }

        var existingX = await context.Contacts
            .AsNoTracking()
            .AnyAsync(c => c.X25519PublicKey == invitation.ContactX25519PublicKey, cancellationToken)
            .ConfigureAwait(false);
        var existingE = await context.Contacts
            .AsNoTracking()
            .AnyAsync(c => c.Ed25519PublicKey == invitation.ContactEd25519PublicKey, cancellationToken)
            .ConfigureAwait(false);
        if (existingX || existingE)
        {
            throw new InvalidOperationException(
                $"PromoteInvitationAsync: contact pubkey already in TrustedContacts.");
        }

        var adminContact = await context.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsAdmin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "PromoteInvitationAsync: no admin TrustedContact in local DB.");

        var systemGroup = await context.ShareGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "PromoteInvitationAsync: system ShareGroup not found in local DB.");

        var adminSystemTarget = await context.ShareTargets
            .AsNoTracking()
            .FirstOrDefaultAsync(t =>
                t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == adminKeys.X25519PublicKey
                && t.KeyVersion == systemGroup.KeyVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "PromoteInvitationAsync: admin's own system ShareTarget not found.");

        var adminWrappedCek = CryptoSyncBootstrap.DeserializeWrappedCek(adminSystemTarget.WrappedContentKey);

        var adminPrivKey = adminKeys.X25519PrivateKey;
        IReadOnlyList<WrappedKey> wrappedForNewMember;
        byte[] systemTargetSig;
        try
        {
            var addResult = await groupEncryption.AddGroupMembersAsync(
                adminPrivKey,
                adminKeys.X25519PublicKey,
                adminWrappedCek,
                [invitation.ContactX25519PublicKey],
                systemGroup.GroupContext).ConfigureAwait(false);
            if (!addResult.Success)
            {
                throw new InvalidOperationException(
                    $"PromoteInvitationAsync: AddGroupMembersAsync failed: {addResult.ErrorCode}");
            }
            wrappedForNewMember = addResult.Value
                ?? throw new InvalidOperationException(
                    "PromoteInvitationAsync: AddGroupMembersAsync returned null.");

            var adminEd25519Priv = adminKeys.Ed25519PrivateKey;
            try
            {
                systemTargetSig = await signer.SignShareTargetAsync(
                    adminEd25519Priv, invitation.ContactX25519PublicKey, systemRole,
                    systemGroup.GroupContext, systemGroup.KeyVersion).ConfigureAwait(false);
            }
            finally
            {
            }
        }
        finally
        {
        }

        var newWrappedCek = wrappedForNewMember.SingleOrDefault(w =>
            w.MemberPublicKey == invitation.ContactX25519PublicKey)
            ?? throw new InvalidOperationException(
                "PromoteInvitationAsync: wrapped key for new member missing.");

        // Whitelist transition pushed BEFORE the local commit. The invitation's
        // transport keypair is revoked (POSTs blocked immediately, GETs allowed
        // within READ_GRACE_SECONDS so the invitee can finish ingesting any
        // in-flight envelopes), and the contact's real Ed25519 hash is added
        // so subsequent sync POSTs from the contact's actual identity hit a
        // whitelisted entry. Single push, version+1, ops in order.
        //
        // Push-first ordering rationale: if the relay push fails after a
        // local commit, the invitation row is gone but the transport key
        // remains whitelisted as 'active' on the relay — an orphaned channel
        // the invitee can keep POSTing through until the next admin action.
        // Push-first inverts the failure surface: a push failure leaves
        // both local AND relay state unchanged (invitation still pending,
        // admin can retry). A push-success / local-commit-fail leaves the
        // relay benignly pre-whitelisting a key with no matching local
        // Contact row — retry-safe because the whitelist ops are idempotent
        // at the relay (Add on existing flips to active, Revoke on revoked
        // is a no-op-equivalent UPDATE).
        var revokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var contactHash = WhitelistPushService.HashPubkey(
            deploymentSaltBase64, invitation.ContactEd25519PublicKey);
        var whitelistOps = invitation.TransportEd25519PublicKey is { } transportKey
            ? new WhitelistOp[]
              {
                  WhitelistOp.Revoke(
                      WhitelistPushService.HashPubkey(deploymentSaltBase64, transportKey),
                      revokedAt),
                  WhitelistOp.Add(contactHash),
              }
            : new WhitelistOp[] { WhitelistOp.Add(contactHash) };
        await PushWhitelistOpsAsync(adminKeys, whitelistOps, cancellationToken).ConfigureAwait(false);

        await using var tx = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var contactId = Guid.NewGuid();
        var contactRow = new TrustedContact
        {
            Id = contactId,
            Username = invitation.Username,
            Email = invitation.Email ?? string.Empty,
            Comment = invitation.Comment,
            X25519PublicKey = invitation.ContactX25519PublicKey,
            Ed25519PublicKey = invitation.ContactEd25519PublicKey,
            IsAdmin = false,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        context.Contacts.Add(contactRow);

        context.ShareGroups.Add(new ShareGroup
        {
            Id = invitation.SelfGroupId.Value,
            GroupContext = invitation.SelfGroupContext,
            KeyVersion = invitation.SelfKeyVersion.Value,
            GroupAdminPublicKey = invitation.ContactX25519PublicKey,
            CreatedAt = now,
            UpdatedAt = now,
            SharingScope = SharingScope.CLIENT,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        });

        context.ShareTargets.Add(new ShareTarget
        {
            Id = Guid.NewGuid(),
            ShareGroupId = invitation.SelfGroupId.Value,
            KeyVersion = invitation.SelfKeyVersion.Value,
            MemberPublicKey = invitation.ContactX25519PublicKey,
            WrappedContentKey = invitation.SelfWrappedContentKey,
            Role = SyncRole.OWNER,
            AdminSignature = invitation.SelfShareTargetSignature,
            GroupAdminEd25519PublicKey = invitation.ContactEd25519PublicKey,
            GrantedByContactId = contactId,
            UpdatedAt = now,
            SharingScope = SharingScope.CLIENT,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        });

        context.ShareTargets.Add(new ShareTarget
        {
            Id = Guid.NewGuid(),
            ShareGroupId = systemGroup.Id,
            KeyVersion = systemGroup.KeyVersion,
            MemberPublicKey = invitation.ContactX25519PublicKey,
            WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(newWrappedCek.WrappedContentKey),
            Role = systemRole,
            AdminSignature = systemTargetSig,
            GroupAdminEd25519PublicKey = adminContact.Ed25519PublicKey,
            GrantedByContactId = adminContact.Id,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        });

        await DeleteInvitationChannelAsync(invitation, cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return contactRow;
    }
}
