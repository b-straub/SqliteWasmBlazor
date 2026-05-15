using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Signed declaration that a member voluntarily left a group.
/// Persisted alongside the soft-deleted <see cref="ShareTarget"/> so any
/// peer can verify the leave was voluntary (not forged by another member).
///
/// <para>
/// Canonical payload signed by the member:
/// <c>groupContext | keyVersion | "leave"</c>
/// </para>
/// </summary>
[SystemTable]
public sealed class LeaveDeclaration : SyncableEntity
{
    /// <summary>GroupContext of the group being left.</summary>
    [MaxLength(256)]
    public required string GroupContext { get; set; }

    /// <summary>KeyVersion at the time of leaving.</summary>
    public int KeyVersion { get; set; }

    /// <summary>Ed25519 public key (Base64) of the member who left.</summary>
    [MaxLength(64)]
    public required string MemberEd25519PublicKey { get; set; }

    /// <summary>
    /// Ed25519 signature over the canonical payload. Verified against
    /// <see cref="MemberEd25519PublicKey"/> to prove the leave was voluntary.
    /// </summary>
    public required byte[] Signature { get; set; }
}
