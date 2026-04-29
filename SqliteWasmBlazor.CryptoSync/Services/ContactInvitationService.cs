using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Admin-initiated invitation flow. Admin calls
/// <see cref="CreateInvitationAsync"/> to generate a transport keypair +
/// <see cref="ShareGroup"/> + <see cref="Invitation"/> row, then ships the
/// returned <see cref="InvitationBundle"/> out-of-band. Invitee calls
/// <see cref="RespondToInvitationAsync"/> with their own keys to ECDH-encrypt
/// a signed response back. Admin then drains the inbox via
/// <see cref="IngestInvitationResponsesAsync"/> and promotes a responded
/// invitation to a real <see cref="TrustedContact"/> via
/// <see cref="PromoteInvitationAsync"/>.
///
/// <para>
/// <b>Privacy claim:</b> the admin holds the contact's self-group rows but
/// cannot unwrap the CEK — re-deriving the wrapping key requires the
/// contact's X25519 private key. <c>Client</c>-scoped rows the contact
/// creates later are opaque to the admin from day one.
/// </para>
/// </summary>
public class ContactInvitationService(
    CryptoSyncContextBase context,
    IGroupEncryption groupEncryption,
    ICryptoProvider crypto,
    DeclarationSigner signer,
    IWhitelistPushService whitelistPush)
{
    /// <summary>
    /// Build the contact's privacy-preserving self-group rows: random CEK
    /// wrapped via <c>HKDF(ECDH(contactPriv, contactPub), info=selfGroupContext)</c>
    /// (only the contact can re-derive the wrapping key) plus the
    /// ShareTarget credential signature.
    /// </summary>
    private async ValueTask<ContactSelfGroupMaterial> BuildContactSelfGroupAsync(
        DualKeyPairFull contactKeys, Guid contactId)
    {
        var selfGroupContext = CryptoSyncBootstrap.BuildSelfGroupContext(contactId);
        var contactPrivKey = Convert.FromBase64String(contactKeys.X25519PrivateKey);
        try
        {
            var bundleResult = await groupEncryption.CreateGroupKeysAsync(
                contactPrivKey,
                contactKeys.X25519PublicKey,
                [contactKeys.X25519PublicKey],
                selfGroupContext).ConfigureAwait(false);

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

            var contactEd25519Priv = Convert.FromBase64String(contactKeys.Ed25519PrivateKey);
            byte[] selfTargetSig;
            try
            {
                selfTargetSig = await signer.SignShareTargetAsync(
                    contactEd25519Priv, contactKeys.X25519PublicKey,
                    SyncRole.OWNER, selfGroupContext, bundle.KeyVersion).ConfigureAwait(false);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(contactEd25519Priv);
            }

            return new ContactSelfGroupMaterial(
                selfGroupContext, bundle.KeyVersion, wrapped, selfTargetSig);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(contactPrivKey);
        }
    }

    private readonly record struct ContactSelfGroupMaterial(
        string GroupContext,
        int KeyVersion,
        byte[] WrappedCek,
        byte[] ShareTargetSignature);

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
        var transportPub = transportDual.X25519PublicKey;
        var transportEd25519Pub = transportDual.Ed25519PublicKey;

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

        // Whitelist the transport Ed25519 pubkey so the invitee's POSTs hit
        // the relay during the bootstrap window. Pushed at LastWhitelistVersion+1;
        // on 409 (concurrent admin push), align local cursor to the relay's
        // current_version and retry. Local invitation rows are already
        // committed — caller can `RevokeInvitationAsync` to clean up if push
        // ultimately fails.
        var transportHash = WhitelistPushService.HashPubkey(deploymentSaltBase64, transportEd25519Pub);
        await PushWhitelistOpsAsync(
            adminKeys,
            [WhitelistOp.Add(transportHash)],
            cancellationToken).ConfigureAwait(false);

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

    private ValueTask PushWhitelistOpsAsync(
        DualKeyPairFull adminKeys,
        IReadOnlyList<WhitelistOp> ops,
        CancellationToken cancellationToken)
        => WhitelistAdminFlow.PushAsync(whitelistPush, context, adminKeys, ops, cancellationToken);

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
    /// Contact-side: respond to an admin's invitation. Verifies the bundle's
    /// admin signature + expiry, derives the transport keypair from the
    /// shared secret, generates the contact's self-group rows locally, signs
    /// the canonical
    /// <c>InvitationId || ContactX25519 || ContactEd25519 || ExpiresAt.Ticks</c>
    /// payload with the contact's Ed25519 key, AES-GCM-encrypts the response
    /// under <c>HKDF(ECDH(transportPriv, adminX25519Pub), info=invitationGroupContext)</c>,
    /// and broadcasts the envelope via <paramref name="syncTransport"/>. The
    /// admin claims it through <see cref="IngestInvitationResponsesAsync"/>;
    /// other broadcast readers fail to unwrap and drop silently.
    /// </summary>
    public async ValueTask RespondToInvitationAsync(
        InvitationBundle bundle,
        DualKeyPairFull contactKeys,
        ContactUserData userData,
        ISyncTransport syncTransport,
        Guid? proposedContactId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(contactKeys);
        ArgumentNullException.ThrowIfNull(syncTransport);

        // 1. Derive both transport keypairs from the shared secret. The
        // X25519 part drives the ECDH for AES-GCM-encrypting the response
        // payload; the Ed25519 part is what the invitee's relay POST signer
        // will use during the bootstrap window (whitelisted by admin).
        var transportDual = await crypto.DeriveDualKeyPairAsync(bundle.TransportSecret).ConfigureAwait(false);
        var transportPub = transportDual.X25519PublicKey;

        // 2. Verify admin's signature on the bundle.
        var canonicalBundle = BuildBundleCanonical(transportPub, bundle.GroupId, bundle.ExpiresAt);
        var bundleOk = await crypto.VerifyAsync(
            canonicalBundle,
            Convert.ToBase64String(bundle.AdminSignature),
            bundle.AdminEd25519PublicKey).ConfigureAwait(false);
        if (!bundleOk)
        {
            throw new InvalidInvitationBundleException(
                "InvitationBundle.AdminSignature failed Ed25519 verification.");
        }

        // 3. Verify expiry.
        if (DateTime.UtcNow >= bundle.ExpiresAt)
        {
            throw new InvitationExpiredException(
                $"InvitationBundle expired at {bundle.ExpiresAt:O}.");
        }

        // 4. Build the contact's self-group rows (privacy invariant — admin can't unwrap).
        var contactId = proposedContactId ?? Guid.NewGuid();
        var selfMaterial = await BuildContactSelfGroupAsync(contactKeys, contactId).ConfigureAwait(false);
        var selfGroupId = Guid.NewGuid();

        // 5. Sign canonical (InvitationId || ContactX25519 || ContactEd25519 || ExpiresAt.Ticks).
        var canonicalContact = BuildContactSignatureCanonical(
            bundle.GroupId, contactKeys.X25519PublicKey, contactKeys.Ed25519PublicKey, bundle.ExpiresAt);
        var contactEd25519Priv = Convert.FromBase64String(contactKeys.Ed25519PrivateKey);
        byte[] contactSig;
        try
        {
            var signResult = await crypto.SignAsync(canonicalContact, contactEd25519Priv).ConfigureAwait(false);
            if (!signResult.Success || signResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"ContactInvitationService.RespondToInvitationAsync: SignAsync failed: {signResult.ErrorCode}");
            }
            contactSig = Convert.FromBase64String(signResult.Value);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(contactEd25519Priv);
        }

        var payload = new InvitationResponsePayload
        {
            ContactX25519PublicKey = contactKeys.X25519PublicKey,
            ContactEd25519PublicKey = contactKeys.Ed25519PublicKey,
            SelfGroupId = selfGroupId,
            SelfGroupContext = selfMaterial.GroupContext,
            SelfKeyVersion = selfMaterial.KeyVersion,
            SelfWrappedContentKey = selfMaterial.WrappedCek,
            SelfShareTargetSignature = selfMaterial.ShareTargetSignature,
            ContactSignature = contactSig
        };
        var payloadBytes = MessagePackSerializer.Serialize(payload);

        // 6. AES-GCM under HKDF(ECDH(transportPriv, adminPub), info=invitationGroupContext).
        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var transportPriv = Convert.FromBase64String(transportDual.X25519PrivateKey);
        SymmetricEncryptedData encrypted;
        try
        {
            var wkResult = await crypto.DeriveWrappingKeyAsync(
                transportPriv, bundle.AdminX25519PublicKey, groupContext).ConfigureAwait(false);
            if (!wkResult.Success)
            {
                throw new InvalidOperationException(
                    $"ContactInvitationService.RespondToInvitationAsync: DeriveWrappingKeyAsync failed: {wkResult.ErrorCode}");
            }
            var encResult = await crypto.EncryptSymmetricAsync(
                Convert.ToBase64String(payloadBytes), wkResult.Value).ConfigureAwait(false);
            if (!encResult.Success || encResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"ContactInvitationService.RespondToInvitationAsync: EncryptSymmetricAsync failed: {encResult.ErrorCode}");
            }
            encrypted = encResult.Value;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(transportPriv);
        }

        // 7. Build envelope + broadcast through the transport. The admin
        // ingests via IngestInvitationResponsesAsync; non-admins drop on
        // unwrap-fail since the AES-GCM key is HKDF(ECDH(transport, admin)).
        var envelope = new InvitationResponseEnvelope
        {
            GroupId = bundle.GroupId,
            Ciphertext = Convert.FromBase64String(encrypted.Ciphertext),
            Nonce = Convert.FromBase64String(encrypted.Nonce)
        };
        var envelopeBytes = MessagePackSerializer.Serialize(envelope);

        await syncTransport.SendAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the canonical bytes the invitee signs over for
    /// <see cref="InvitationResponsePayload.ContactSignature"/>:
    /// <c>InvitationId || ContactX25519 || ContactEd25519 || ExpiresAt.Ticks</c>,
    /// returned as Base64 for the string-based crypto API.
    /// </summary>
    internal static string BuildContactSignatureCanonical(
        Guid invitationId, string contactX25519PubKey, string contactEd25519PubKey, DateTime expiresAt)
    {
        var idBytes = invitationId.ToByteArray();
        var x = Convert.FromBase64String(contactX25519PubKey);
        var e = Convert.FromBase64String(contactEd25519PubKey);
        var ticks = BitConverter.GetBytes(expiresAt.Ticks);
        var canonical = new byte[idBytes.Length + x.Length + e.Length + ticks.Length];
        var offset = 0;
        Buffer.BlockCopy(idBytes, 0, canonical, offset, idBytes.Length); offset += idBytes.Length;
        Buffer.BlockCopy(x, 0, canonical, offset, x.Length); offset += x.Length;
        Buffer.BlockCopy(e, 0, canonical, offset, e.Length); offset += e.Length;
        Buffer.BlockCopy(ticks, 0, canonical, offset, ticks.Length);
        return Convert.ToBase64String(canonical);
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

            var adminPriv = Convert.FromBase64String(adminKeys.X25519PrivateKey);
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
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminPriv);
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

        var adminPrivKey = Convert.FromBase64String(adminKeys.X25519PrivateKey);
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

            var adminEd25519Priv = Convert.FromBase64String(adminKeys.Ed25519PrivateKey);
            try
            {
                systemTargetSig = await signer.SignShareTargetAsync(
                    adminEd25519Priv, invitation.ContactX25519PublicKey, systemRole,
                    systemGroup.GroupContext, systemGroup.KeyVersion).ConfigureAwait(false);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminEd25519Priv);
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminPrivKey);
        }

        var newWrappedCek = wrappedForNewMember.SingleOrDefault(w =>
            w.MemberPublicKey == invitation.ContactX25519PublicKey)
            ?? throw new InvalidOperationException(
                "PromoteInvitationAsync: wrapped key for new member missing.");

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

        // Whitelist transition: revoke the transport keypair (POSTs as it
        // are blocked immediately, GETs allowed within READ_GRACE_SECONDS so
        // the invitee can finish ingesting any in-flight envelopes), and add
        // the contact's real Ed25519 hash so subsequent sync POSTs from the
        // contact's actual identity hit a whitelisted entry. Single push,
        // version+1, ops in order.
        if (invitation.TransportEd25519PublicKey is not null)
        {
            var transportHash = WhitelistPushService.HashPubkey(
                deploymentSaltBase64, invitation.TransportEd25519PublicKey);
            var contactHash = WhitelistPushService.HashPubkey(
                deploymentSaltBase64, invitation.ContactEd25519PublicKey);
            var revokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await PushWhitelistOpsAsync(
                adminKeys,
                [
                    WhitelistOp.Revoke(transportHash, revokedAt),
                    WhitelistOp.Add(contactHash),
                ],
                cancellationToken).ConfigureAwait(false);
        }

        return contactRow;
    }
}
