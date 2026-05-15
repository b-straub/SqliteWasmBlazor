namespace SqliteWasmBlazor;

/// <summary>
/// Typed metadata for worker-side encrypted bulk export.
///
/// <para>
/// The caller supplies a per-table spec list. Each entry tells the worker
/// which table to encrypt, the WHERE clause to filter rows (e.g.
/// <c>"UpdatedAt &gt; ?"</c> for delta exports — or <c>null</c> for a full
/// snapshot), and whether the table should be stamped as a system-table
/// group on the wire (drives admin verification on import).
/// </para>
///
/// <para>
/// Constructing the WHERE clause on the C# side (rather than pushing a
/// single timestamp to the worker) keeps the call flexible for future
/// filters beyond <c>UpdatedAt</c> — composite predicates, per-table
/// custom scope filters, etc. The schema source of truth the caller uses
/// to enumerate tables is the local <c>ColumnRegistry</c> DbSet on
/// <c>CryptoSyncContextBase</c>.
/// </para>
/// </summary>
public record BulkExportMetadata
{
    /// <summary>Seed = 0, Delta = 1.</summary>
    public int Mode { get; init; }

    /// <summary>
    /// Per-table export specs in the order the worker should process them.
    /// Callers typically order system-first so import staggering works.
    /// Used by the encrypted delta path.
    /// </summary>
    public IReadOnlyList<TableExportSpec> Tables { get; init; } = [];

    // --- Plain-path fields (legacy non-encrypted export). Unused by the
    // encrypted delta path — present only so existing plain-path callers
    // continue to compile.
    public string? TableName { get; init; }
    public string[][]? Columns { get; init; }
    public string? PrimaryKeyColumn { get; init; }
    public string? SchemaHash { get; init; }
    public string? DataType { get; init; }
    public string? AppIdentifier { get; init; }
    public string? Where { get; init; }
    public string[]? WhereParams { get; init; }
    public string? OrderBy { get; init; }
}

/// <summary>
/// One entry in a <see cref="BulkExportMetadata.Tables"/> list — tells the
/// worker which rows of which table to encrypt into a <c>ShadowRowGroup</c>.
/// </summary>
public record TableExportSpec
{
    /// <summary>Open table name (DbSet name, e.g. "CryptoTestItems").</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Optional SQL WHERE clause (without the WHERE keyword) appended to
    /// the per-table SELECT. Use positional <c>?</c> placeholders bound
    /// from <see cref="WhereParams"/>. <c>null</c> = full-table snapshot.
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Parameters bound to the <see cref="Where"/> clause in positional order.
    /// </summary>
    public IReadOnlyList<string>? WhereParams { get; init; }

    /// <summary>
    /// True if this is a <c>[SystemTable]</c> — admin-only writes on import,
    /// and the group gets stamped so the importer staggers it ahead of
    /// domain groups.
    /// </summary>
    public bool IsSystemTable { get; init; }
}
