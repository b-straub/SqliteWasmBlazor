namespace SqliteWasmBlazor;

/// <summary>
/// Typed metadata for worker-side bulk import from raw MessagePack row data.
/// Used with BulkImportRawAsync — the caller provides column metadata separately
/// from the row data, avoiding the need to embed a V2 header in the payload.
/// </summary>
public record BulkImportMetadata
{
    /// <summary>Target SQL table name (e.g., "ShoppingItems")</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Column metadata: [[propertyName, sqlType, csharpType], ...]
    /// Maps MessagePack [Key(n)] indices to SQL columns with type conversion info.
    /// Use MessagePackFileHeaderV2.BuildColumnMetadata() to generate this.
    /// </summary>
    public required string[][] Columns { get; init; }

    /// <summary>Primary key column name for ON CONFLICT clauses</summary>
    public required string PrimaryKeyColumn { get; init; }
}
