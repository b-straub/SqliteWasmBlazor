using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Per-client wrapped content key for a sharing scope.
/// The content key decrypts rows in the _crypto_ table for this scope.
/// Each participant gets the content key wrapped with their X25519 public key (ECIES).
/// System table — admin-managed (and Owner-managed for owned domain scopes).
/// </summary>
[SystemTable]
public sealed class SharingKey
{
    public Guid Id { get; set; }

    /// <summary>Scope identifier (matches SyncableEntity.SharingId).</summary>
    [MaxLength(128)]
    public required string SharingId { get; set; }

    /// <summary>Sharing scope type.</summary>
    public SharingScope SharingScope { get; set; }

    /// <summary>FK to TrustedContact — who this key is for.</summary>
    public Guid ClientContactId { get; set; }

    /// <summary>Navigation: the contact this key belongs to.</summary>
    public TrustedContact? ClientContact { get; set; }

    /// <summary>Content key wrapped with client's X25519 public key (ECIES). Binary blob.</summary>
    public required byte[] WrappedContentKey { get; set; }

    /// <summary>Role assigned to this client for this scope.</summary>
    public SyncRole Role { get; set; }

    /// <summary>FK to TrustedContact — who granted access.</summary>
    public Guid GrantedByContactId { get; set; }

    /// <summary>Navigation: the contact who granted access.</summary>
    public TrustedContact? GrantedByContact { get; set; }

    public DateTime CreatedAt { get; set; }
}
