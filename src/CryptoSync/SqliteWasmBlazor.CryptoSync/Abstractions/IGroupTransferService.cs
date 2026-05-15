using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// Two-phase group ownership transfer (protocol op O8). Splits the handoff so
/// the new admin doesn't have to be online when the old admin releases:
/// <list type="bullet">
///   <item><b>Phase 1 — Release</b> (<see cref="ReleaseGroupAsync"/>): old admin
///         signs a <see cref="TransferDeclaration"/> and wraps the current
///         CEK for the new admin. Unilateral.</item>
///   <item><b>Phase 2 — Claim</b> (<see cref="ClaimGroupAsync"/>): new admin
///         rotates keys, re-wraps for all members, signs fresh ShareTargets,
///         and takes ownership.</item>
/// </list>
/// Between phases the group is in a transitional state: existing CEK still
/// works for all members, but no membership changes until the claim completes.
/// </summary>
public interface IGroupTransferService
{
    /// <summary>
    /// Phase 1 — Release: old GroupAdmin signs a transfer declaration and
    /// wraps the current CEK for the new admin. The old admin is done after
    /// this call.
    /// </summary>
    ValueTask<TransferDeclaration> ReleaseGroupAsync(
        DualKeyPairFull oldAdminKeys,
        string newAdminX25519PublicKey,
        string newAdminEd25519PublicKey,
        string groupContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 2 — Claim: new GroupAdmin rotates keys, re-wraps for all members,
    /// signs fresh ShareTargets, and takes ownership.
    /// </summary>
    ValueTask<ShareGroup> ClaimGroupAsync(
        DualKeyPairFull newAdminKeys,
        string groupContext,
        CancellationToken cancellationToken = default);
}
