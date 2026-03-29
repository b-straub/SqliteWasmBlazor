namespace SqliteWasmBlazor;

/// <summary>
/// Strategy for resolving conflicts during worker-side bulk import.
/// Values match the worker bulkImport conflictStrategy parameter.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>
    /// Plain INSERT — no conflict handling (seed/full import into empty database).
    /// </summary>
    None = 0,

    /// <summary>
    /// Most recent UpdatedAt timestamp wins.
    /// ON CONFLICT DO UPDATE WHERE excluded.UpdatedAt > table.UpdatedAt
    /// </summary>
    LastWriteWins = 1,

    /// <summary>
    /// Local changes always win.
    /// ON CONFLICT DO NOTHING — only inserts new items.
    /// </summary>
    LocalWins = 2,

    /// <summary>
    /// Imported (delta) changes always win.
    /// ON CONFLICT DO UPDATE — always overwrites local items.
    /// </summary>
    DeltaWins = 3
}
