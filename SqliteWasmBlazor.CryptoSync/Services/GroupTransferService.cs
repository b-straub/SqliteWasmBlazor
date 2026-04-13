using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using BlazorPRF.Crypto.Abstractions.Services;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Two-phase group ownership transfer (O8).
///
/// <para>
/// <b>Phase 1 — Release</b> (<see cref="ReleaseGroupAsync"/>): Old GroupAdmin
/// signs a <see cref="TransferDeclaration"/>, wraps the current CEK for the
/// new admin via <c>ECDH(oldAdminPriv, newAdminX25519Pub)</c>, persists both.
/// Unilateral — new admin does not need to be online.
/// </para>
///
/// <para>
/// <b>Phase 2 — Claim</b> (<see cref="ClaimGroupAsync"/>): New GroupAdmin
/// receives the delta, unwraps the CEK, rotates keys (new KeyVersion, new
/// CEK, re-wraps for all members), signs fresh ShareTargets, updates
/// <see cref="ShareGroup.GroupAdminPublicKey"/>.
/// </para>
///
/// <para>
/// Between phases the group is in a transitional state — existing CEK works
/// for all members, but no membership changes until the claim completes.
/// </para>
/// </summary>
public class GroupTransferService(
    CryptoSyncContextBase context,
    IGroupEncryption groupEncryption,
    DeclarationSigner signer)
{
    /// <summary>
    /// Phase 1 — Release: old GroupAdmin signs a transfer declaration and
    /// wraps the current CEK for the new admin. The old admin is done after
    /// this call.
    /// </summary>
    /// <param name="oldAdminKeys">Old GroupAdmin's full keypair.</param>
    /// <param name="newAdminX25519PublicKey">New admin's X25519 public key (Base64).</param>
    /// <param name="newAdminEd25519PublicKey">New admin's Ed25519 public key (Base64).</param>
    /// <param name="groupContext">GroupContext of the group to transfer.</param>
    /// <returns>The persisted <see cref="TransferDeclaration"/>.</returns>
    public async ValueTask<TransferDeclaration> ReleaseGroupAsync(
        DualKeyPairFull oldAdminKeys,
        string newAdminX25519PublicKey,
        string newAdminEd25519PublicKey,
        string groupContext,
        CancellationToken cancellationToken = default)
    {
        var group = await context.ShareGroups
            .SingleOrDefaultAsync(g => g.GroupContext == groupContext, cancellationToken)
            ?? throw new InvalidOperationException(
                $"GroupTransferService: ShareGroup '{groupContext}' not found");

        if (group.GroupAdminPublicKey != oldAdminKeys.X25519PublicKey)
        {
            throw new InvalidOperationException(
                "GroupTransferService: caller is not the GroupAdmin of this group");
        }

        // Check no pending transfer already exists.
        var existing = await context.TransferDeclarations
            .AnyAsync(td => td.GroupContext == groupContext && !td.IsClaimed, cancellationToken);
        if (existing)
        {
            throw new InvalidOperationException(
                $"GroupTransferService: group '{groupContext}' already has a pending transfer");
        }

        // Sign the transfer declaration.
        var ed25519Priv = Convert.FromBase64String(oldAdminKeys.Ed25519PrivateKey);
        byte[] transferSig;
        try
        {
            transferSig = await signer.SignTransferDeclarationAsync(
                ed25519Priv, groupContext, newAdminEd25519PublicKey);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ed25519Priv);
        }

        // Wrap the current CEK for the new admin.
        var oldAdminTarget = await context.ShareTargets
            .SingleOrDefaultAsync(t =>
                t.ShareGroupId == group.Id
                && t.MemberPublicKey == oldAdminKeys.X25519PublicKey
                && t.KeyVersion == group.KeyVersion, cancellationToken)
            ?? throw new InvalidOperationException(
                "GroupTransferService: old admin's ShareTarget not found");

        var oldAdminWrappedCek = CryptoSyncBootstrap.DeserializeWrappedCek(oldAdminTarget.WrappedContentKey);

        var x25519Priv = Convert.FromBase64String(oldAdminKeys.X25519PrivateKey);
        IReadOnlyList<WrappedKey> wrappedForNewAdmin;
        try
        {
            var result = await groupEncryption.AddGroupMembersAsync(
                x25519Priv, oldAdminKeys.X25519PublicKey,
                oldAdminWrappedCek, [newAdminX25519PublicKey], groupContext);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"GroupTransferService: AddGroupMembersAsync failed: {result.ErrorCode}");
            }
            wrappedForNewAdmin = result.Value
                ?? throw new InvalidOperationException(
                    "GroupTransferService: AddGroupMembersAsync returned null");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(x25519Priv);
        }

        var newAdminWrapped = wrappedForNewAdmin[0];

        // Sign the new admin's ShareTarget credential.
        var ed25519PrivForCred = Convert.FromBase64String(oldAdminKeys.Ed25519PrivateKey);
        byte[] credSig;
        try
        {
            credSig = await signer.SignShareTargetAsync(
                ed25519PrivForCred, newAdminX25519PublicKey, SyncRole.OWNER,
                groupContext, group.KeyVersion);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ed25519PrivForCred);
        }

        await using var tx = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var declaration = new TransferDeclaration
        {
            GroupContext = groupContext,
            OldGroupAdminEd25519PublicKey = oldAdminKeys.Ed25519PublicKey,
            NewGroupAdminEd25519PublicKey = newAdminEd25519PublicKey,
            Signature = transferSig,
            IsClaimed = false,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        context.TransferDeclarations.Add(declaration);

        // Update or create a ShareTarget for the new admin.
        var newAdminContact = await context.Contacts
            .SingleOrDefaultAsync(c => c.Ed25519PublicKey == newAdminEd25519PublicKey, cancellationToken)
            ?? throw new InvalidOperationException(
                "GroupTransferService: new admin is not a known contact");

        var existingTarget = await context.ShareTargets
            .SingleOrDefaultAsync(t =>
                t.ShareGroupId == group.Id
                && t.MemberPublicKey == newAdminX25519PublicKey
                && t.KeyVersion == group.KeyVersion, cancellationToken);

        if (existingTarget is not null)
        {
            existingTarget.Role = SyncRole.OWNER;
            existingTarget.WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(newAdminWrapped.WrappedContentKey);
            existingTarget.AdminSignature = credSig;
            existingTarget.GroupAdminEd25519PublicKey = oldAdminKeys.Ed25519PublicKey;
        }
        else
        {
            context.ShareTargets.Add(new ShareTarget
            {
                ShareGroupId = group.Id,
                KeyVersion = group.KeyVersion,
                MemberPublicKey = newAdminX25519PublicKey,
                WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(newAdminWrapped.WrappedContentKey),
                Role = SyncRole.OWNER,
                AdminSignature = credSig,
                GroupAdminEd25519PublicKey = oldAdminKeys.Ed25519PublicKey,
                GrantedByContactId = newAdminContact.Id,
                SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return declaration;
    }

    /// <summary>
    /// Phase 2 — Claim: new GroupAdmin rotates keys, re-wraps for all members,
    /// signs fresh ShareTargets, and takes ownership.
    /// </summary>
    /// <param name="newAdminKeys">New GroupAdmin's full keypair.</param>
    /// <param name="groupContext">GroupContext of the group to claim.</param>
    /// <returns>The updated <see cref="ShareGroup"/> with new KeyVersion and admin key.</returns>
    public async ValueTask<ShareGroup> ClaimGroupAsync(
        DualKeyPairFull newAdminKeys,
        string groupContext,
        CancellationToken cancellationToken = default)
    {
        var declaration = await context.TransferDeclarations
            .SingleOrDefaultAsync(td =>
                td.GroupContext == groupContext
                && td.NewGroupAdminEd25519PublicKey == newAdminKeys.Ed25519PublicKey
                && !td.IsClaimed, cancellationToken)
            ?? throw new InvalidOperationException(
                $"GroupTransferService: no pending transfer for '{groupContext}' to this admin");

        // Verify the transfer declaration signature.
        var sigValid = await signer.VerifyTransferDeclarationAsync(
            declaration.OldGroupAdminEd25519PublicKey,
            groupContext,
            newAdminKeys.Ed25519PublicKey,
            declaration.Signature);
        if (!sigValid)
        {
            throw new InvalidOperationException(
                "GroupTransferService: TransferDeclaration signature is invalid");
        }

        var group = await context.ShareGroups
            .SingleAsync(g => g.GroupContext == groupContext, cancellationToken);

        // Collect all current members' X25519 public keys (for re-wrapping).
        var memberKeys = await context.ShareTargets
            .Where(t => t.ShareGroupId == group.Id && t.KeyVersion == group.KeyVersion)
            .Select(t => t.MemberPublicKey)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Rotate: new CEK, new KeyVersion, re-wrap for all members.
        var newKeyVersion = group.KeyVersion + 1;
        var newGroupContext = groupContext; // GroupContext stays the same — KeyVersion changes.

        var x25519Priv = Convert.FromBase64String(newAdminKeys.X25519PrivateKey);
        GroupKeyBundle rotatedBundle;
        try
        {
            var result = await groupEncryption.RotateGroupKeyAsync(
                x25519Priv, newAdminKeys.X25519PublicKey,
                memberKeys, newGroupContext);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"GroupTransferService: RotateGroupKeyAsync failed: {result.ErrorCode}");
            }
            rotatedBundle = result.Value
                ?? throw new InvalidOperationException(
                    "GroupTransferService: RotateGroupKeyAsync returned null");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(x25519Priv);
        }

        await using var tx = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Update ShareGroup with new admin and key version.
        group.GroupAdminPublicKey = newAdminKeys.X25519PublicKey;
        group.KeyVersion = newKeyVersion;

        // Sign and insert fresh ShareTargets for all members at the new KeyVersion.
        var ed25519Priv = Convert.FromBase64String(newAdminKeys.Ed25519PrivateKey);
        try
        {
            foreach (var memberWrapped in rotatedBundle.MemberKeys)
            {
                var credSig = await signer.SignShareTargetAsync(
                    ed25519Priv, memberWrapped.MemberPublicKey, SyncRole.OWNER,
                    newGroupContext, newKeyVersion);

                var grantedByContact = await context.Contacts
                    .SingleOrDefaultAsync(c => c.X25519PublicKey == memberWrapped.MemberPublicKey, cancellationToken);

                context.ShareTargets.Add(new ShareTarget
                {
                    ShareGroupId = group.Id,
                    KeyVersion = newKeyVersion,
                    MemberPublicKey = memberWrapped.MemberPublicKey,
                    WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(memberWrapped.WrappedContentKey),
                    Role = SyncRole.OWNER, // TODO: preserve original roles from old ShareTargets
                    AdminSignature = credSig,
                    GroupAdminEd25519PublicKey = newAdminKeys.Ed25519PublicKey,
                    GrantedByContactId = grantedByContact?.Id ?? Guid.Empty,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = CryptoSyncBootstrap.SystemSharingId
                });
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ed25519Priv);
        }

        // Mark the transfer as claimed.
        declaration.IsClaimed = true;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return group;
    }
}
