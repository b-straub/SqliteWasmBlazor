using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Tracks an invitation created by the admin device. Local-only — only the admin
/// creates invitations (decision §12) and we do not sync admin's invitation
/// tracking across multiple admin devices yet, so this entity does not inherit
/// <see cref="SyncableEntity"/>.
/// </summary>
[SystemTable]
public sealed class SentInvitation
{
    public Guid Id { get; set; }

    [MaxLength(32)]
    public required string InviteCode { get; set; }

    /// <summary>Plain email address of the invitee. Local-only, never leaves the admin device.</summary>
    [MaxLength(256)]
    public required string Email { get; set; }

    /// <summary>Full armored invite for re-sending if needed.</summary>
    [MaxLength(8192)]
    public required string ArmoredInvite { get; set; }

    public InviteStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? TrustedContactId { get; set; }
    public TrustedContact? TrustedContact { get; set; }
}
