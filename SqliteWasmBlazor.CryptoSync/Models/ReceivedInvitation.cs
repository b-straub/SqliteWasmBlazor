namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Tracks an invitation that was accepted by the user. Local-only on each
/// receiving device — does not inherit <see cref="SyncableEntity"/> and does not
/// flow over the wire.
/// </summary>
[SystemTable]
public sealed class ReceivedInvitation
{
    public Guid Id { get; set; }
    public required string InviteCode { get; set; }

    /// <summary>Ed25519 public key of the inviter (Base64).</summary>
    public required string InviterEd25519PublicKey { get; set; }

    public DateTime AcceptedAt { get; set; }
    public Guid? TrustedContactId { get; set; }
    public TrustedContact? TrustedContact { get; set; }
}
