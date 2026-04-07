using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// A verified contact with public keys for encryption and signature verification.
/// System table — only the admin device creates contacts; other devices receive them
/// via the public-scope sync once promoted to <see cref="TrustLevel.Full"/>.
/// </summary>
[SystemTable]
public sealed class TrustedContact : SyncableEntity
{
    [MaxLength(128)]
    public required string Username { get; set; }

    [MaxLength(256)]
    public required string Email { get; set; }

    [MaxLength(512)]
    public string? Comment { get; set; }

    /// <summary>X25519 public key (Base64) for asymmetric encryption.</summary>
    [MaxLength(64)]
    public required string X25519PublicKey { get; set; }

    /// <summary>Ed25519 public key (Base64) for signature verification.</summary>
    [MaxLength(64)]
    public required string Ed25519PublicKey { get; set; }

    /// <summary>
    /// Global default role suggested for this contact. Per-scope role lives on
    /// <c>SharingKey.Role</c> and is what actually governs runtime permission lookups.
    /// </summary>
    public SyncRole Role { get; set; }

    public TrustLevel TrustLevel { get; set; }

    public TrustDirection Direction { get; set; }

    public DateTime VerifiedAt { get; set; }

    // Note: TrustedContact inherits SyncableEntity, so SharingScope is the row's scope.
    // While TrustLevel == Marginal it stays Client (admin-private). On promotion to
    // Full via ContactPromotionService.ElevateToFullAsync the inherited SharingScope
    // flips to Public + SharingId becomes the system public id, causing the next sync
    // to broadcast this contact under the public content key to all Full peers.
}

/// <summary>
/// Plain user data for a contact. Stored as plain columns on <see cref="TrustedContact"/>.
/// No column-level encryption — at-rest defense for sensitive entities is the
/// shadow-only <c>[Sensitive]</c> pattern, not per-column encryption (see Phase H).
/// </summary>
public sealed class ContactUserData
{
    public required string Username { get; init; }
    public required string Email { get; init; }
    public string? Comment { get; init; }
}
