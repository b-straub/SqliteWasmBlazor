using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Runtime column metadata for the worker's import path. One row per column
/// per syncable entity. Seeded by the source generator in
/// <c>ConfigureCryptoTables</c> — the generator walks entity properties at
/// compile time and emits <c>HasData</c> rows so the worker can query column
/// names, types, and order at import time without any domain knowledge.
///
/// <para>
/// The schema itself is not a secret at runtime — only the row DATA is
/// encrypted during transport. Column metadata is plaintext in the local DB.
/// </para>
/// </summary>
[SystemTable]
public sealed class ColumnRegistryEntry
{
    /// <summary>Deterministic PK derived from TableName + ColumnIndex.</summary>
    public Guid Id { get; set; }

    /// <summary>Open table name (DbSet name, e.g. "CryptoTestItems").</summary>
    [MaxLength(128)]
    public required string TableName { get; set; }

    /// <summary>Column position in export order (0-based).</summary>
    public int ColumnIndex { get; set; }

    /// <summary>Column name matching the entity property name.</summary>
    [MaxLength(128)]
    public required string ColumnName { get; set; }

    /// <summary>SQLite column type (TEXT, INTEGER, REAL, BLOB).</summary>
    [MaxLength(16)]
    public required string SqlType { get; set; }

    /// <summary>C# type name (Guid, String, Int32, DateTime, etc.).</summary>
    [MaxLength(64)]
    public required string CSharpType { get; set; }

    /// <summary>True if this column is the primary key.</summary>
    public bool IsPrimaryKey { get; set; }
}
