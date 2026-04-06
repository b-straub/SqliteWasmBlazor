using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Per-client wrapped content key for a sharing scope.
/// The content key decrypts rows in the _crypto_ table for this scope.
/// Each participant gets the content key wrapped with their X25519 public key (ECIES).
/// </summary>
public sealed class SharingKey
{
    public Guid Id { get; set; }

    /// <summary>Scope identifier (matches ISyncableEntity.SharingId).</summary>
    [MaxLength(128)]
    public required string SharingId { get; set; }

    /// <summary>Sharing scope type.</summary>
    public SharingScope SharingScope { get; set; }

    /// <summary>Client's Ed25519 public key (Base64) — identifies who this key is for.</summary>
    [MaxLength(64)]
    public required string ClientEd25519PublicKey { get; set; }

    /// <summary>Content key wrapped with client's X25519 public key (ECIES). Binary blob.</summary>
    public required byte[] WrappedContentKey { get; set; }

    /// <summary>Role assigned to this client for this scope.</summary>
    public SyncRole Role { get; set; }

    /// <summary>Ed25519 public key of who granted access (for verification).</summary>
    [MaxLength(64)]
    public required string GrantedByEd25519PublicKey { get; set; }

    public DateTime CreatedAt { get; set; }
}
