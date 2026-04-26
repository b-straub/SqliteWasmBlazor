using BlazorPRF.Crypto.Abstractions.Models;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Voluntary leave from a shared group (O7). The member signs a
/// <see cref="LeaveDeclaration"/>, soft-deletes their own
/// <see cref="ShareTarget"/>, and persists both in one transaction.
///
/// <para>
/// The <see cref="LeaveDeclaration"/> proves to any peer that the leave
/// was voluntary — it can be verified against the member's
/// <see cref="TrustedContact.Ed25519PublicKey"/>. No one else can forge it.
/// </para>
///
/// <para>
/// After the leave, the GroupAdmin should rotate keys (O6) to
/// cryptographically lock out the departed member. The leave itself is
/// cooperative — it does not revoke key material.
/// </para>
/// </summary>
public class LeaveService(
    CryptoSyncContextBase context,
    DeclarationSigner signer)
{
    /// <summary>
    /// Leave a group: sign a <see cref="LeaveDeclaration"/> and soft-delete
    /// the member's own <see cref="ShareTarget"/>.
    /// </summary>
    /// <param name="memberKeys">The leaving member's full keypair.</param>
    /// <param name="groupContext">GroupContext of the group to leave.</param>
    /// <returns>The persisted <see cref="LeaveDeclaration"/>.</returns>
    public async ValueTask<LeaveDeclaration> LeaveGroupAsync(
        DualKeyPairFull memberKeys,
        string groupContext,
        CancellationToken cancellationToken = default)
    {
        // Find the member's ShareTarget for this group.
        var shareTarget = await context.ShareTargets
            .SingleOrDefaultAsync(t =>
                t.MemberPublicKey == memberKeys.X25519PublicKey
                && context.ShareGroups.Any(g =>
                    g.Id == t.ShareGroupId
                    && g.GroupContext == groupContext
                    && g.KeyVersion == t.KeyVersion),
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"LeaveService: no ShareTarget found for member in group '{groupContext}'");

        var group = await context.ShareGroups
            .SingleAsync(g => g.Id == shareTarget.ShareGroupId, cancellationToken);

        // Sign the leave declaration.
        var ed25519Priv = Convert.FromBase64String(memberKeys.Ed25519PrivateKey);
        byte[] signature;
        try
        {
            signature = await signer.SignLeaveDeclarationAsync(
                ed25519Priv, groupContext, group.KeyVersion);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ed25519Priv);
        }

        await using var tx = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Persist the declaration.
        var declaration = new LeaveDeclaration
        {
            GroupContext = groupContext,
            KeyVersion = group.KeyVersion,
            MemberEd25519PublicKey = memberKeys.Ed25519PublicKey,
            Signature = signature,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        context.LeaveDeclarations.Add(declaration);

        // Soft-delete the member's ShareTarget.
        shareTarget.IsDeleted = true;
        shareTarget.DeletedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return declaration;
    }

    /// <summary>
    /// Verify a <see cref="LeaveDeclaration"/> is authentic — the signature
    /// matches the claimed member's Ed25519 public key.
    /// </summary>
    public async ValueTask<bool> VerifyLeaveDeclarationAsync(LeaveDeclaration declaration)
    {
        return await signer.VerifyLeaveDeclarationAsync(
            declaration.MemberEd25519PublicKey,
            declaration.GroupContext,
            declaration.KeyVersion,
            declaration.Signature);
    }
}
