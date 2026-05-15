using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// A contact with public keys for encryption and signature verification.
/// System table — only the admin device creates contacts; other devices
/// receive them via the system-scope sync.
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

    /// <summary>X25519 public key (Base64) for asymmetric encryption / key agreement.</summary>
    [MaxLength(64)]
    public required string X25519PublicKey { get; set; }

    /// <summary>Ed25519 public key (Base64) for signature verification.</summary>
    [MaxLength(64)]
    public required string Ed25519PublicKey { get; set; }

    /// <summary>True if this contact is the instance admin (creator).</summary>
    public bool IsAdmin { get; set; }
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
