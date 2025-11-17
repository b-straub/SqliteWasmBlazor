namespace SqliteWasmBlazor.Models.Enums;

/// <summary>
/// Defines the export mode for data export operations
/// </summary>
public enum ExportMode
{
    /// <summary>
    /// Export all data (full database export)
    /// </summary>
    Full,

    /// <summary>
    /// Export only changed data since last sync (delta export)
    /// </summary>
    Delta
}
