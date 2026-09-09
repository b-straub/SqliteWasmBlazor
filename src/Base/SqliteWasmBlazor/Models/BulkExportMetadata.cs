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

    // --- Plain-path fields. These describe a single table and mirror the V2
    // MessagePack header the worker reads (this package's
    // MessagePackFileHeaderV2). The encrypted delta path ignores every one of
    // them — it takes its per-table spec from Tables instead.

    /// <summary>
    /// Plain path only. Name of the single table to export. The encrypted
    /// delta path ignores this and reads <see cref="Tables"/>.
    /// </summary>
    public string? TableName { get; init; }

    /// <summary>
    /// Plain path only. Column metadata, one entry per column as
    /// <c>[columnName, sqlType, csharpType]</c>, in the order the serialized
    /// row items appear. The worker builds its INSERT from this rather than
    /// reflecting over the row shape, which is what lets it run without any
    /// C#-side type knowledge.
    /// </summary>
    public string[][]? Columns { get; init; }

    /// <summary>
    /// Plain path only. Primary key column name, used for the
    /// <c>ON CONFLICT</c> clause on import. Everything else becomes the
    /// <c>SET</c> list of the upsert, so naming the wrong column here makes
    /// an import insert duplicates instead of updating.
    /// </summary>
    public string? PrimaryKeyColumn { get; init; }

    /// <summary>
    /// Plain path only. Hash of the exporting schema, checked on import
    /// against the importing schema. A mismatch fails the import rather than
    /// writing rows whose columns no longer line up.
    /// </summary>
    public string? SchemaHash { get; init; }

    /// <summary>
    /// Plain path only. Fully-qualified CLR type name of the exported entity,
    /// checked on import so a file cannot be loaded into the wrong table.
    /// </summary>
    public string? DataType { get; init; }

    /// <summary>
    /// Plain path only. Optional application identifier stamped into the
    /// export. When the importer is given one to expect, a mismatch fails the
    /// import — a guard against loading another app's file.
    /// </summary>
    public string? AppIdentifier { get; init; }

    /// <summary>
    /// Plain path only. SQL WHERE clause without the <c>WHERE</c> keyword,
    /// using positional <c>?</c> placeholders bound from
    /// <see cref="WhereParams"/>. <c>null</c> exports the whole table.
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Plain path only. Values bound to <see cref="Where"/>'s placeholders in
    /// positional order.
    /// </summary>
    public string[]? WhereParams { get; init; }

    /// <summary>
    /// Plain path only. SQL ORDER BY clause without the <c>ORDER BY</c>
    /// keyword. <c>null</c> leaves row order unspecified, which is what
    /// SQLite gives you without one.
    /// </summary>
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
