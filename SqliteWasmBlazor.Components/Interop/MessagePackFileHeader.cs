using MessagePack;

namespace SqliteWasmBlazor.Components.Interop;

/// <summary>
/// Header written at the beginning of MessagePack export files for schema validation
/// Prevents importing incompatible data from different apps or schema versions
/// </summary>
[MessagePackObject]
public class MessagePackFileHeader
{
    /// <summary>
    /// Magic number to identify MessagePack export files from this library
    /// "SWBMP" = SqliteWasmBlazor MessagePack
    /// </summary>
    [Key(0)]
    public string MagicNumber { get; set; } = "SWBMP";

    /// <summary>
    /// Schema hash (computed from MessagePack [Key] attributes and property types)
    /// Automatically detects schema changes - no manual versioning needed
    /// 16-character hex string (first 64 bits of SHA256)
    /// </summary>
    [Key(1)]
    public string SchemaHash { get; set; } = string.Empty;

    /// <summary>
    /// Full type name of serialized data (e.g., "SqliteWasmBlazor.Models.DTOs.TodoItemDto")
    /// Used to ensure import matches expected type
    /// </summary>
    [Key(2)]
    public string DataType { get; set; } = string.Empty;

    /// <summary>
    /// Application identifier (optional - for multi-app scenarios)
    /// </summary>
    [Key(3)]
    public string? AppIdentifier { get; set; }

    /// <summary>
    /// Timestamp when export was created (UTC)
    /// </summary>
    [Key(4)]
    public DateTime ExportedAt { get; set; }

    /// <summary>
    /// Total record count (for validation)
    /// </summary>
    [Key(5)]
    public int RecordCount { get; set; }

    /// <summary>
    /// Validate that this header is compatible with expected schema
    /// </summary>
    /// <param name="expectedType">Expected data type</param>
    /// <param name="expectedSchemaHash">Expected schema hash (or null to skip schema check)</param>
    /// <param name="expectedAppId">Expected app identifier (or null to skip app check)</param>
    /// <exception cref="InvalidOperationException">Thrown if header validation fails</exception>
    public void Validate(string expectedType, string? expectedSchemaHash = null, string? expectedAppId = null)
    {
        if (MagicNumber != "SWBMP")
        {
            throw new InvalidOperationException(
                $"Invalid file format: expected MessagePack export file (magic number 'SWBMP'), got '{MagicNumber}'");
        }

        if (DataType != expectedType)
        {
            throw new InvalidOperationException(
                $"Incompatible data type: expected '{expectedType}', file contains '{DataType}'");
        }

        if (expectedSchemaHash is not null && SchemaHash != expectedSchemaHash)
        {
            throw new InvalidOperationException(
                $"Incompatible schema: export schema (hash {SchemaHash}) does not match current schema (hash {expectedSchemaHash}). " +
                $"The export file structure may have changed. Expected: {expectedSchemaHash}, Got: {SchemaHash}");
        }

        if (expectedAppId is not null && AppIdentifier != expectedAppId)
        {
            throw new InvalidOperationException(
                $"Incompatible application: expected '{expectedAppId}', file is from '{AppIdentifier}'");
        }

        if (RecordCount < 0)
        {
            throw new InvalidOperationException($"Invalid record count: {RecordCount}");
        }
    }

    /// <summary>
    /// Create a header for export
    /// Schema hash is computed automatically from the type's MessagePack structure
    /// </summary>
    public static MessagePackFileHeader Create<T>(int recordCount, string? appIdentifier = null)
    {
        return new MessagePackFileHeader
        {
            MagicNumber = "SWBMP",
            SchemaHash = SchemaHashGenerator.ComputeHash<T>(),
            DataType = typeof(T).FullName ?? typeof(T).Name,
            AppIdentifier = appIdentifier,
            ExportedAt = DateTime.UtcNow,
            RecordCount = recordCount
        };
    }
}
