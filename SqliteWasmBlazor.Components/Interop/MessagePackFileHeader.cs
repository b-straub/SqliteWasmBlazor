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
    /// Schema version (incremented when TodoItemDto structure changes)
    /// Format: MAJOR.MINOR (e.g., "1.0")
    /// - MAJOR: Breaking changes (incompatible schema)
    /// - MINOR: Non-breaking changes (optional fields added)
    /// </summary>
    [Key(1)]
    public string SchemaVersion { get; set; } = string.Empty;

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
    /// <param name="expectedVersion">Expected schema version (or null to skip version check)</param>
    /// <param name="expectedAppId">Expected app identifier (or null to skip app check)</param>
    /// <exception cref="InvalidOperationException">Thrown if header validation fails</exception>
    public void Validate(string expectedType, string? expectedVersion = null, string? expectedAppId = null)
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

        if (expectedVersion is not null && SchemaVersion != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Incompatible schema version: expected '{expectedVersion}', file has '{SchemaVersion}'. " +
                $"This file may be from a different version of the application.");
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
    /// </summary>
    public static MessagePackFileHeader Create<T>(int recordCount, string schemaVersion, string? appIdentifier = null)
    {
        return new MessagePackFileHeader
        {
            MagicNumber = "SWBMP",
            SchemaVersion = schemaVersion,
            DataType = typeof(T).FullName ?? typeof(T).Name,
            AppIdentifier = appIdentifier,
            ExportedAt = DateTime.UtcNow,
            RecordCount = recordCount
        };
    }
}
