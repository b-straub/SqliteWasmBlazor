namespace SqliteWasmBlazor.Models.Enums;

/// <summary>
/// Strategy for resolving conflicts when importing delta data.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>
    /// Most recent UpdatedAt timestamp wins (default).
    /// Compares timestamps and keeps the newer version.
    /// </summary>
    LastWriteWins,

    /// <summary>
    /// Local changes always win.
    /// Imported items are only added if they don't exist locally.
    /// </summary>
    LocalWins,

    /// <summary>
    /// Imported (delta) changes always win.
    /// Local items are always overwritten by imported items.
    /// </summary>
    DeltaWins
}
