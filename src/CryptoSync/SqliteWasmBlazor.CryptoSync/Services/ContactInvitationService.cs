using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.CryptoSync.Abstractions;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto;

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
///
/// <para>
/// <b>File layout.</b> This main file owns the primary ctor + shared private
/// helpers (self-group build, canonical-bytes builders, channel cleanup,
/// wrapping-key wipe). Admin-side public commands live in
/// <c>ContactInvitationService.Admin.cs</c>; the invitee-side Respond
/// command lives in <c>ContactInvitationService.Invitee.cs</c>.
/// </para>
/// </summary>
internal partial class ContactInvitationService(
    CryptoSyncContextBase context,
    IGroupEncryption groupEncryption,
    ICryptoProvider crypto,
    DeclarationSigner signer,
    IWhitelistPushService whitelistPush) : IContactInvitationService
{
    /// <summary>
    /// Default invitation TTL. Bundles past <c>UtcNow + DefaultInvitationTtl</c>
    /// from <see cref="CreateInvitationAsync"/> are rejected on response.
    /// </summary>
    public static readonly TimeSpan DefaultInvitationTtl = TimeSpan.FromHours(24);

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
        var contactPrivKey = contactKeys.X25519PrivateKey;
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

            var contactEd25519Priv = contactKeys.Ed25519PrivateKey;
            byte[] selfTargetSig;
            try
            {
                selfTargetSig = await signer.SignShareTargetAsync(
                    contactEd25519Priv, contactKeys.X25519PublicKey,
                    SyncRole.OWNER, selfGroupContext, bundle.KeyVersion).ConfigureAwait(false);
            }
            finally
            {
            }

            return new ContactSelfGroupMaterial(
                selfGroupContext, bundle.KeyVersion, wrapped, selfTargetSig);
        }
        finally
        {
        }
    }

    private readonly record struct ContactSelfGroupMaterial(
        string GroupContext,
        int KeyVersion,
        byte[] WrappedCek,
        byte[] ShareTargetSignature);

    private ValueTask PushWhitelistOpsAsync(
        DualKeyPairFull adminKeys,
        IReadOnlyList<WhitelistOp> ops,
        CancellationToken cancellationToken)
        => WhitelistAdminFlow.PushAsync(whitelistPush, context, adminKeys, ops, cancellationToken);

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
    /// Best-effort zeroize of an HKDF-derived wrapping key. Mirrors
    /// <c>GroupEncryptionService.ClearMemory</c> — the underlying buffer comes
    /// from <c>Convert.FromBase64String(...)</c> in <see cref="SubtleCryptoProvider"/>
    /// so it backs onto an array we can clear.
    /// </summary>
    private static void ClearWrappingKey(ReadOnlyMemory<byte> wrappingKey)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(wrappingKey, out var seg) && seg.Array is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seg.AsSpan());
        }
    }
}
