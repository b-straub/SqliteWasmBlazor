using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Two-sided invitation flow for adding a new <see cref="TrustedContact"/>
/// with a privacy-preserving self-group.
///
/// <list type="bullet">
///   <item>
///     <see cref="BuildInvitationResponseAsync"/> — runs on the invitee's
///     device. Generates the contact's self-group CEK locally, wraps it via
///     <c>HKDF(ECDH(contactPriv, contactPub), info=selfGroupContext)</c>
///     (a key only the contact can re-derive), assembles a signed
///     <see cref="ContactAcceptancePayload"/>. The plaintext CEK never
///     leaves this method's scope.
///   </item>
///   <item>
///     <see cref="AcceptInvitationResponseAsync"/> — runs on the admin's
///     device. Verifies the contact's signature, persists the contact +
///     contact-self-group rows + admin-wrapped system-group target for the
///     new contact, all inside a single DB transaction.
///   </item>
/// </list>
///
/// <para>
/// <b>Privacy claim:</b> the admin holds the contact's self-group rows but
/// cannot unwrap the CEK inside the wrapped blob — re-deriving the
/// wrapping key requires <c>contactPriv</c>, which the admin does not have.
/// <c>Client</c>-scoped rows the contact creates later are opaque to the
/// admin from day one.
/// </para>
/// </summary>
public class ContactInvitationService(
    CryptoSyncContextBase context,
    IGroupEncryption groupEncryption,
    ICryptoProvider crypto,
    DeclarationSigner signer)
{
    /// <summary>
    /// Contact-side: build a signed acceptance payload for delivery to the
    /// admin (out-of-band: QR, file, relay, …). The contact's self-group
    /// rows + wrapped CEK are pre-built so the admin can persist them
    /// without ever holding the plaintext CEK.
    /// </summary>
    public async ValueTask<ContactAcceptancePayload> BuildInvitationResponseAsync(
        DualKeyPairFull contactKeys,
        ContactUserData userData,
        Guid? proposedContactId = null,
        CancellationToken cancellationToken = default)
    {
        var contactId = proposedContactId ?? Guid.NewGuid();
        var selfGroupContext = CryptoSyncBootstrap.BuildSelfGroupContext(contactId);

        var contactPrivKey = Convert.FromBase64String(contactKeys.X25519PrivateKey);
        try
        {
            var bundleResult = await groupEncryption.CreateGroupKeysAsync(
                contactPrivKey,
                contactKeys.X25519PublicKey,
                [contactKeys.X25519PublicKey],
                selfGroupContext);

            if (!bundleResult.Success)
            {
                throw new InvalidOperationException(
                    $"ContactInvitationService: CreateGroupKeysAsync (self-group) failed: {bundleResult.ErrorCode}");
            }
            var bundle = bundleResult.Value
                ?? throw new InvalidOperationException(
                    "ContactInvitationService: CreateGroupKeysAsync returned null bundle");
            if (bundle.MemberKeys.Count == 0)
            {
                throw new InvalidOperationException(
                    "ContactInvitationService: CreateGroupKeysAsync returned empty MemberKeys");
            }

            var wrapped = CryptoSyncBootstrap.SerializeWrappedCek(bundle.MemberKeys[0].WrappedContentKey);

            // Sign the self-ShareTarget credential: contact is the GroupAdmin
            // of their own self-group, so they sign their own Role grant.
            var contactEd25519PrivForCred = Convert.FromBase64String(contactKeys.Ed25519PrivateKey);
            byte[] selfTargetSig;
            try
            {
                selfTargetSig = await signer.SignShareTargetAsync(
                    contactEd25519PrivForCred, contactKeys.X25519PublicKey,
                    SyncRole.OWNER, selfGroupContext, bundle.KeyVersion);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(contactEd25519PrivForCred);
            }

            var payload = new ContactAcceptancePayload
            {
                ContactId = contactId,
                Username = userData.Username,
                Email = userData.Email,
                Comment = userData.Comment,
                X25519PublicKey = contactKeys.X25519PublicKey,
                Ed25519PublicKey = contactKeys.Ed25519PublicKey,
                SelfGroupId = Guid.NewGuid(),
                SelfGroupContext = selfGroupContext,
                SelfKeyVersion = bundle.KeyVersion,
                SelfWrappedContentKey = wrapped,
                SelfShareTargetSignature = selfTargetSig
            };

            // Sign the canonical bytes (signature field empty), then store
            // the signature. Verification on the admin side does the same
            // clear → serialize → verify → restore dance.
            var canonical = MessagePackSerializer.Serialize(payload);
            var signedCanonical = Convert.ToBase64String(canonical);
            var contactEd25519Priv = Convert.FromBase64String(contactKeys.Ed25519PrivateKey);
            try
            {
                var signResult = await crypto.SignAsync(signedCanonical, contactEd25519Priv);
                if (!signResult.Success || signResult.Value is null)
                {
                    throw new InvalidOperationException(
                        $"ContactInvitationService: SignAsync failed: {signResult.ErrorCode}");
                }
                payload.AcceptancePayloadSignature = Convert.FromBase64String(signResult.Value);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(contactEd25519Priv);
            }

            return payload;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(contactPrivKey);
        }
    }

    /// <summary>
    /// Default invitation TTL. Bundles past <c>UtcNow + DefaultInvitationTtl</c>
    /// from <see cref="CreateInvitationAsync"/> are rejected on response.
    /// </summary>
    public static readonly TimeSpan DefaultInvitationTtl = TimeSpan.FromHours(24);

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
        string username,
        string? email = null,
        string? comment = null,
        string? relayHint = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var now = DateTime.UtcNow;
        var expiresAt = now + (ttl ?? DefaultInvitationTtl);
        var groupId = Guid.NewGuid();
        var groupContext = $"invitation-{groupId:N}:v1";

        // Generate transport secret + derive transport keypair.
        var transportSecret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var transportKeyPair = await crypto.DeriveX25519KeyPairAsync(transportSecret).ConfigureAwait(false);
        var transportPub = transportKeyPair.PublicKeyBase64;

        // Create the invitation share group with admin + transportPub as members.
        var adminPriv = Convert.FromBase64String(adminKeys.X25519PrivateKey);
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
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminPriv);
        }

        var adminWrapped = wrappedKeys.Single(k => k.MemberPublicKey == adminKeys.X25519PublicKey);
        var transportWrapped = wrappedKeys.Single(k => k.MemberPublicKey == transportPub);

        // Sign ShareTarget credentials with admin's Ed25519 key.
        var adminEd25519Priv = Convert.FromBase64String(adminKeys.Ed25519PrivateKey);
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
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminEd25519Priv);
        }

        // Look up admin's contact id (for ShareTarget.GrantedByContactId).
        var adminContact = await context.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsAdmin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "ContactInvitationService.CreateInvitationAsync: no admin TrustedContact found in local DB.");

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

        context.Invitations.Add(new Invitation
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            Comment = comment,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            UpdatedAt = now,
            SharingScope = SharingScope.SHARED,
            SharingId = groupContext
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
            RelayHint = relayHint
        };
    }

    /// <summary>
    /// Hard-delete invitations whose <see cref="Invitation.ExpiresAt"/> is in
    /// the past. Cleans up the invitation share group + ShareTargets too.
    /// </summary>
    public async ValueTask DeleteExpiredInvitationsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var expired = await context.Invitations
            .Where(i => i.ExpiresAt <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return;
        }

        foreach (var invitation in expired)
        {
            await DeleteInvitationChannelAsync(invitation, cancellationToken).ConfigureAwait(false);
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hard-delete a single invitation by id (admin revoke). Removes the
    /// invitation share group + both ShareTargets + the Invitation row.
    /// </summary>
    public async ValueTask RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        var invitation = await context.Invitations
            .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"ContactInvitationService.RevokeInvitationAsync: invitation {invitationId} not found.");
        await DeleteInvitationChannelAsync(invitation, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the canonical bytes the admin signs for the bundle:
    /// <c>transportPub || GroupId.ToByteArray() || ExpiresAt.Ticks</c>,
    /// returned as Base64 for the string-based crypto API.
    /// </summary>
    internal static string BuildBundleCanonical(string transportPub, Guid groupId, DateTime expiresAt)
    {
        var transportPubBytes = Convert.FromBase64String(transportPub);
        var groupIdBytes = groupId.ToByteArray();
        var ticks = BitConverter.GetBytes(expiresAt.Ticks);
        var canonical = new byte[transportPubBytes.Length + groupIdBytes.Length + ticks.Length];
        Buffer.BlockCopy(transportPubBytes, 0, canonical, 0, transportPubBytes.Length);
        Buffer.BlockCopy(groupIdBytes, 0, canonical, transportPubBytes.Length, groupIdBytes.Length);
        Buffer.BlockCopy(ticks, 0, canonical, transportPubBytes.Length + groupIdBytes.Length, ticks.Length);
        return Convert.ToBase64String(canonical);
    }

    /// <summary>
    /// Contact-side: respond to an admin invitation by pulling the channel
    /// rows, filling in the Invitation row with the contact's identity, and
    /// pushing the modified row back through standard sync. Rewritten in
    /// commit 3 — current body intentionally throws.
    /// </summary>
    public ValueTask RespondToInvitationAsync(
        InvitationBundle bundle,
        DualKeyPairFull contactKeys,
        ContactUserData userData,
        ISyncTransport syncTransport,
        Guid? proposedContactId = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("RespondToInvitationAsync is rewritten in commit 3 of the invitation pivot.");
    }

    private async ValueTask DeleteInvitationChannelAsync(Invitation invitation, CancellationToken cancellationToken)
    {
        var groupContext = invitation.SharingId;
        var group = await context.ShareGroups
            .FirstOrDefaultAsync(g => g.GroupContext == groupContext, cancellationToken)
            .ConfigureAwait(false);
        if (group is not null)
        {
            var targets = await context.ShareTargets
                .Where(t => t.ShareGroupId == group.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            context.ShareTargets.RemoveRange(targets);
            context.ShareGroups.Remove(group);
        }
        context.Invitations.Remove(invitation);
    }

    /// <summary>
    /// Admin-side: verify a contact's acceptance payload, then atomically
    /// insert (a) the new <see cref="TrustedContact"/> row,
    /// (b) the contact's pre-built self-group <see cref="ShareGroup"/> +
    /// <see cref="ShareTarget"/> (admin cannot unwrap), and (c) a fresh
    /// system-group <see cref="ShareTarget"/> for the new contact wrapped
    /// with the admin's system CEK so they can decrypt public-scope rows.
    /// </summary>
    /// <param name="adminKeys">Admin's full keypair (private keys are zeroed after use).</param>
    /// <param name="payload">The signed payload received from the contact.</param>
    /// <param name="systemRole">Role to assign the new contact in the system group (default = Editor).</param>
    /// <returns>The newly persisted <see cref="TrustedContact"/> row.</returns>
    public async ValueTask<TrustedContact> AcceptInvitationResponseAsync(
        DualKeyPairFull adminKeys,
        ContactAcceptancePayload payload,
        SyncRole systemRole = SyncRole.EDITOR,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // 1. Verify the payload's Ed25519 signature.
        await VerifyPayloadSignatureAsync(payload);

        // 2-8. Atomic insert of all four rows.
        await using var tx = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 2. Insert the contact identity row.
        var now = DateTime.UtcNow;
        var contactRow = new TrustedContact
        {
            Id = payload.ContactId,
            Username = payload.Username,
            Email = payload.Email,
            Comment = payload.Comment,
            X25519PublicKey = payload.X25519PublicKey,
            Ed25519PublicKey = payload.Ed25519PublicKey,
            IsAdmin = false,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        context.Contacts.Add(contactRow);

        // 3. Insert the contact's self-group rows. SharingId routes via
        // the system CEK; the wrapped CEK inside is contact-only.
        var contactSelfGroup = new ShareGroup
        {
            Id = payload.SelfGroupId,
            GroupContext = payload.SelfGroupContext,
            KeyVersion = payload.SelfKeyVersion,
            GroupAdminPublicKey = payload.X25519PublicKey,
            CreatedAt = now,
            UpdatedAt = now,
            SharingScope = SharingScope.CLIENT,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        context.ShareGroups.Add(contactSelfGroup);

        var adminContact = await context.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsAdmin, cancellationToken)
            ?? throw new InvalidOperationException(
                "ContactInvitationService: no admin TrustedContact found in local DB. " +
                "AcceptInvitationResponseAsync must run on a fully bootstrapped admin device.");

        var contactSelfTarget = new ShareTarget
        {
            Id = Guid.NewGuid(),
            ShareGroupId = payload.SelfGroupId,
            KeyVersion = payload.SelfKeyVersion,
            MemberPublicKey = payload.X25519PublicKey,
            WrappedContentKey = payload.SelfWrappedContentKey,
            Role = SyncRole.OWNER,
            AdminSignature = payload.SelfShareTargetSignature,
            GroupAdminEd25519PublicKey = payload.Ed25519PublicKey,
            GrantedByContactId = payload.ContactId,
            UpdatedAt = now,
            SharingScope = SharingScope.CLIENT,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        context.ShareTargets.Add(contactSelfTarget);

        // 4. Wrap the system CEK for the new contact via the admin's
        // private key (admin already has access to the system CEK as Owner).
        var systemGroup = await context.ShareGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext, cancellationToken)
            ?? throw new InvalidOperationException(
                "ContactInvitationService: system ShareGroup row not found in local DB.");

        var adminSystemTarget = await context.ShareTargets
            .AsNoTracking()
            .FirstOrDefaultAsync(t =>
                t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == adminKeys.X25519PublicKey
                && t.KeyVersion == systemGroup.KeyVersion, cancellationToken)
            ?? throw new InvalidOperationException(
                "ContactInvitationService: admin's own system ShareTarget not found.");

        var adminWrappedCek = CryptoSyncBootstrap.DeserializeWrappedCek(adminSystemTarget.WrappedContentKey);

        var adminPrivKey = Convert.FromBase64String(adminKeys.X25519PrivateKey);
        IReadOnlyList<WrappedKey> wrappedForNewMember;
        try
        {
            var addResult = await groupEncryption.AddGroupMembersAsync(
                adminPrivKey,
                adminKeys.X25519PublicKey,
                adminWrappedCek,
                [payload.X25519PublicKey],
                systemGroup.GroupContext);

            if (!addResult.Success)
            {
                throw new InvalidOperationException(
                    $"ContactInvitationService: AddGroupMembersAsync failed: {addResult.ErrorCode}");
            }
            wrappedForNewMember = addResult.Value
                ?? throw new InvalidOperationException(
                    "ContactInvitationService: AddGroupMembersAsync returned null result");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminPrivKey);
        }

        var newMemberWrappedCek = wrappedForNewMember.SingleOrDefault(w =>
            w.MemberPublicKey == payload.X25519PublicKey)
            ?? throw new InvalidOperationException(
                "ContactInvitationService: AddGroupMembersAsync did not return a wrapped key for the new contact.");

        // 5. Insert the new contact's system-group ShareTarget. Wrapped with
        // the system CEK so the contact can decrypt every existing system row.
        // Signed by the admin (system group owner) as a credential.
        var adminEd25519Priv = Convert.FromBase64String(adminKeys.Ed25519PrivateKey);
        byte[] systemTargetSig;
        try
        {
            systemTargetSig = await signer.SignShareTargetAsync(
                adminEd25519Priv, payload.X25519PublicKey, systemRole,
                systemGroup.GroupContext, systemGroup.KeyVersion);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminEd25519Priv);
        }

        var contactSystemTarget = new ShareTarget
        {
            Id = Guid.NewGuid(),
            ShareGroupId = systemGroup.Id,
            KeyVersion = systemGroup.KeyVersion,
            MemberPublicKey = payload.X25519PublicKey,
            WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(newMemberWrappedCek.WrappedContentKey),
            Role = systemRole,
            AdminSignature = systemTargetSig,
            GroupAdminEd25519PublicKey = adminContact.Ed25519PublicKey,
            GrantedByContactId = adminContact.Id,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        context.ShareTargets.Add(contactSystemTarget);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return contactRow;
    }

    /// <summary>
    /// Verify the payload's Ed25519 signature using the canonical clear-and-restore
    /// scheme: temporarily zero <see cref="ContactAcceptancePayload.AcceptancePayloadSignature"/>,
    /// re-serialize, verify against the saved bytes, then restore.
    /// </summary>
    private async Task VerifyPayloadSignatureAsync(ContactAcceptancePayload payload)
    {
        if (payload.AcceptancePayloadSignature.Length == 0)
        {
            throw new InvalidContactSignatureException(
                "ContactAcceptancePayload.AcceptancePayloadSignature is empty.");
        }

        var savedSignature = payload.AcceptancePayloadSignature;
        payload.AcceptancePayloadSignature = [];
        try
        {
            var canonicalBytes = MessagePackSerializer.Serialize(payload);
            var canonicalBase64 = Convert.ToBase64String(canonicalBytes);
            var signatureBase64 = Convert.ToBase64String(savedSignature);
            var ok = await crypto.VerifyAsync(canonicalBase64, signatureBase64, payload.Ed25519PublicKey);
            if (!ok)
            {
                throw new InvalidContactSignatureException(
                    "ContactAcceptancePayload signature failed Ed25519 verification.");
            }
        }
        finally
        {
            payload.AcceptancePayloadSignature = savedSignature;
        }
    }
}

/// <summary>
/// Thrown when <see cref="ContactInvitationService.AcceptInvitationResponseAsync"/>
/// receives a payload whose Ed25519 signature does not verify.
/// </summary>
public sealed class InvalidContactSignatureException(string message) : Exception(message);
