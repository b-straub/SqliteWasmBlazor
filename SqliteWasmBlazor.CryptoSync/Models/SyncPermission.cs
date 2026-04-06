using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Table/column permission per role. System table (admin-defined, seeded via migration).
/// PermissionDiffJson uses the diff format: { "Table": "readonly", "Table.Col": "readwrite" }
/// </summary>
public sealed class SyncPermission : ISyncableEntity
{
    public Guid Id { get; set; }
    public SyncRole Role { get; set; }

    [MaxLength(128)]
    public required string TableName { get; set; }

    /// <summary>
    /// JSON-serialized permission diff. Empty object {} = full readwrite.
    /// </summary>
    [MaxLength(4096)]
    public required string PermissionDiffJson { get; set; }

    /// <summary>Admin's Ed25519 signature over the permission (Base64).</summary>
    [MaxLength(128)]
    public string? AdminSignature { get; set; }

    /// <summary>Admin's Ed25519 public key (Base64) for verification.</summary>
    [MaxLength(64)]
    public string? AdminPublicKey { get; set; }

    // ISyncableEntity
    public SharingScope SharingScope { get; set; } = SharingScope.Public;
    public string SharingId { get; set; } = "system";
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
