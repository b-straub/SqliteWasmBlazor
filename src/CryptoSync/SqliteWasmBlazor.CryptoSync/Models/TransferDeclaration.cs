using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Signed declaration that a GroupAdmin is transferring group ownership to
/// another contact. Phase 1 (Release) creates this row; Phase 2 (Claim)
/// resolves it by rotating keys and updating the group.
///
/// <para>
/// Between phases the group is in a transitional state — read-only for
/// membership changes, existing CEK still works for all members.
/// </para>
///
/// <para>
/// Canonical payload signed by the old GroupAdmin:
/// <c>groupContext | "transfer-admin" | newGroupAdminEd25519PublicKey</c>
/// </para>
/// </summary>
[SystemTable]
public sealed class TransferDeclaration : SyncableEntity
{
    /// <summary>GroupContext of the group being transferred.</summary>
    [MaxLength(256)]
    public required string GroupContext { get; set; }

    /// <summary>Ed25519 public key (Base64) of the old GroupAdmin who initiated the transfer.</summary>
    [MaxLength(64)]
    public required string OldGroupAdminEd25519PublicKey { get; set; }

    /// <summary>Ed25519 public key (Base64) of the new GroupAdmin receiving ownership.</summary>
    [MaxLength(64)]
    public required string NewGroupAdminEd25519PublicKey { get; set; }

    /// <summary>
    /// Ed25519 signature by the old GroupAdmin over the canonical payload.
    /// Proves the transfer was authorized by the current owner.
    /// </summary>
    public required byte[] Signature { get; set; }

    /// <summary>
    /// Whether Phase 2 (Claim) has been completed. Set to true when the
    /// new GroupAdmin rotates keys and takes ownership.
    /// </summary>
    public bool IsClaimed { get; set; }
}
