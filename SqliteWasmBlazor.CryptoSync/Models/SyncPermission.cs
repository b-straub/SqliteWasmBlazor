using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Table/column/record permission per role. System table (admin-defined, seeded via migration
/// from <c>[Permissions]</c> attributes; mutated at runtime by <c>PermissionService</c>).
///
/// <para>
/// <see cref="PermissionDiffJson"/> uses the nested-per-table format (schema version
/// <see cref="PermissionDiffSchemaVersion"/>):
/// <c>{ "Tbl": { "delete": "deny", "insert": "deny", "columns": { "Price": "readonly" } } }</c>
/// </para>
///
/// <para>
/// Lookup order at runtime: <c>(Table, RecordId=this.Id)</c> first, fall back to
/// <c>(Table, RecordId=NULL)</c>. <c>RecordId == null</c> means a table-wide rule;
/// non-null means a per-row write-lock that overrides the table-wide rule for that one row.
/// </para>
/// </summary>
[SystemTable]
public sealed class SyncPermission : SyncableEntity
{
    /// <summary>Schema version of the nested PermissionDiffJson format.</summary>
    public const int PermissionDiffSchemaVersion = 2;

    public SyncRole Role { get; set; }

    [MaxLength(128)]
    public required string TableName { get; set; }

    /// <summary>
    /// Optional record-level write-lock target. NULL = table-wide rule.
    /// Non-null = override for that one row in <see cref="TableName"/>.
    /// </summary>
    public Guid? RecordId { get; set; }

    /// <summary>
    /// JSON-serialized permission diff. Empty object <c>{}</c> = full readwrite.
    /// Format documented on the class summary.
    /// </summary>
    [MaxLength(4096)]
    public required string PermissionDiffJson { get; set; }

    /// <summary>Admin's Ed25519 signature over the permission (Base64).</summary>
    [MaxLength(128)]
    public string? AdminSignature { get; set; }

    /// <summary>Admin's Ed25519 public key (Base64) for verification.</summary>
    [MaxLength(64)]
    public string? AdminPublicKey { get; set; }

    // SyncableEntity defaults for system table
    // SharingScope = Public, SharingId = "system" (set in seed data)
}
