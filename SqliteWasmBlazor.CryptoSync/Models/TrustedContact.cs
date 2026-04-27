using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Lifecycle state of a <see cref="TrustedContact"/> in the admin-initiated
/// invitation flow.
/// </summary>
public enum ContactStatus
{
    /// <summary>Placeholder created by admin; <see cref="TrustedContact.InvitationToken"/>
    /// is outstanding, contact pubkeys are not yet bound.</summary>
    Invited = 0,

    /// <summary>Contact responded, signature verified, pubkeys bound.
    /// Crypto trust established but no human-side fingerprint check yet.</summary>
    Verified = 1,

    /// <summary>Verified contact whose fingerprint has been confirmed F2F.
    /// Optional final state — Stage 4e.</summary>
    Trusted = 2,

    /// <summary>Admin revoked this contact. Trust chain GC follows.</summary>
    Revoked = 3
}

/// <summary>
/// A contact with public keys for encryption and signature verification.
/// System table — only the admin device creates contacts; other devices
/// receive them via the system-scope sync.
///
/// <para>
/// Invitation flow (admin-initiated): admin calls
/// <c>ContactsService.CreateInvitationAsync</c>, which inserts a placeholder
/// row in <see cref="ContactStatus.Invited"/> with a random
/// <see cref="InvitationToken"/> and no contact pubkeys. The invitation
/// bundle (token + admin pubkey) ships out-of-band. The contact replies
/// through <c>ISyncTransport</c>; admin matches the response by token,
/// fills in <see cref="X25519PublicKey"/> + <see cref="Ed25519PublicKey"/>,
/// transitions to <see cref="ContactStatus.Verified"/>, and clears the token.
/// </para>
/// </summary>
[SystemTable]
public sealed class TrustedContact : SyncableEntity
{
    [MaxLength(128)]
    public required string Username { get; set; }

    /// <summary>Display email. Null on placeholder rows where the admin
    /// didn't supply one — the contact's response payload provides the
    /// authoritative value at <see cref="ContactStatus.Verified"/> bind.</summary>
    [MaxLength(256)]
    public string? Email { get; set; }

    [MaxLength(512)]
    public string? Comment { get; set; }

    /// <summary>X25519 public key (Base64) for asymmetric encryption / key agreement.
    /// Null on placeholder rows in <see cref="ContactStatus.Invited"/> — bound
    /// at the <see cref="ContactStatus.Verified"/> transition. SQLite UNIQUE
    /// allows multiple null rows, so concurrent placeholders coexist safely.</summary>
    [MaxLength(64)]
    public string? X25519PublicKey { get; set; }

    /// <summary>Ed25519 public key (Base64) for signature verification.
    /// Null on placeholder rows; bound at <see cref="ContactStatus.Verified"/>.</summary>
    [MaxLength(64)]
    public string? Ed25519PublicKey { get; set; }

    /// <summary>True if this contact is the instance admin (creator).</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Lifecycle status of the contact in the invitation flow.</summary>
    public ContactStatus Status { get; set; }

    /// <summary>One-shot 32-byte secret bound to this invitation. Doubles as
    /// match key (admin finds the placeholder by token) and PSK (contact
    /// AES-GCM-encrypts the response payload using a token-derived key).
    /// Cleared on <see cref="ContactStatus.Verified"/> bind. Null on contacts
    /// that were never created via the admin-initiated flow.</summary>
    [MaxLength(32)]
    public byte[]? InvitationToken { get; set; }

    /// <summary>UTC timestamp when the placeholder row was created.</summary>
    public DateTime? InvitedAt { get; set; }

    /// <summary>UTC timestamp of the <see cref="ContactStatus.Verified"/> transition.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>UTC timestamp of the <see cref="ContactStatus.Trusted"/> transition (F2F handshake).</summary>
    public DateTime? TrustedAt { get; set; }
}

/// <summary>
/// Plain user data for creating a contact.
/// </summary>
public sealed class ContactUserData
{
    public required string Username { get; init; }
    public required string Email { get; init; }
    public string? Comment { get; init; }
}
