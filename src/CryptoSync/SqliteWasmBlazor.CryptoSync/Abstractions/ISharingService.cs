namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// Cascade-soft-delete helper for syncable subtrees. The
/// <see cref="CryptoSyncSaveChangesInterceptor"/>'s <c>Deleted</c> branch
/// does the same thing for an EF <c>Remove()</c>; this surface is the
/// imperative entry point for callers that want to soft-delete a subtree
/// without going through the EF change tracker.
/// </summary>
public interface ISharingService
{
    /// <summary>
    /// Soft-delete a parent row and every <see cref="SyncableEntity"/>
    /// descendant reachable via foreign keys.
    /// </summary>
    /// <param name="parentTableName">Open-table name of the parent entity (e.g. <c>"CryptoTestLists"</c>).</param>
    /// <param name="parentId">Primary key of the parent row.</param>
    /// <returns>Total number of rows soft-deleted (parent + descendants).</returns>
    Task<int> UnshareAsync(
        string parentTableName,
        Guid parentId,
        CancellationToken cancellationToken = default);
}
