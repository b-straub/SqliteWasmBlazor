using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Fully resolved table/column permission per role. System table seeded by the
/// source generator from <c>[Permissions]</c>, <c>[AllowUpdate]</c>, and
/// <c>[DenyUpdate]</c> attributes. The generator resolves all attribute stacking
/// at compile time — no runtime JSON parsing needed.
///
/// <para>
/// Lookup order: <c>(Table, RecordId=this.Id)</c> first, fall back to
/// <c>(Table, RecordId=NULL)</c>. <c>RecordId == null</c> means a table-wide rule;
/// non-null means a per-row write-lock that overrides the table-wide rule.
/// </para>
/// </summary>
/// <summary>Compile-time immutable — seeded by generator, identical on every client, not synced.</summary>
public sealed class SyncPermission
{
    public Guid Id { get; set; }

    public SharingScope SharingScope { get; set; }

    public string SharingId { get; set; } = "";

    public DateTime UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public SyncRole Role { get; set; }

    [MaxLength(128)]
    public required string TableName { get; set; }

    /// <summary>
    /// Optional record-level write-lock target. NULL = table-wide rule.
    /// Non-null = override for that one row in <see cref="TableName"/>.
    /// </summary>
    public Guid? RecordId { get; set; }

    // Fully resolved CRUD flags — true = allowed, false = denied.
    public bool CanInsert { get; set; }
    public bool CanRead { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }

    /// <summary>
    /// Comma-separated column names that this role may NOT update,
    /// even when <see cref="CanUpdate"/> is true.
    /// Empty string = no column restrictions.
    /// </summary>
    [MaxLength(2048)]
    public string ReadonlyColumns { get; set; } = "";

    /// <summary>
    /// Comma-separated column names that this role MAY update,
    /// even when <see cref="CanUpdate"/> is false.
    /// Empty string = no column overrides.
    /// </summary>
    [MaxLength(2048)]
    public string ReadwriteColumns { get; set; } = "";

}
