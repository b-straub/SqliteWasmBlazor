using SqliteWasmBlazor.CryptoSync.Abstractions;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Cascade-soft-delete helper for syncable subtrees. The previous in-place
/// <c>ShareAsync</c> rewrite-SharingId-on-the-subtree path was deleted in
/// favor of the immutable-<c>SharingId</c> invariant — moves between groups
/// must go through a future <c>TransferService</c> (tombstone source +
/// recreate with fresh Ids in target). What remains is the symmetric
/// <c>UnshareAsync</c> which simply tombstones a subtree.
/// </summary>
internal class SharingService(CryptoSyncContextBase context) : ISharingService
{
    /// <summary>
    /// Soft-delete a parent row and every <see cref="SyncableEntity"/>
    /// descendant reachable via foreign keys. The interceptor's
    /// <c>Deleted</c> branch does the same thing for an EF
    /// <c>Remove()</c>; this is the imperative entry point for callers
    /// that want to soft-delete a subtree without going through the
    /// EF change tracker.
    /// </summary>
    /// <param name="parentTableName">Open-table name of the parent entity (e.g. <c>"CryptoTestLists"</c>).</param>
    /// <param name="parentId">Primary key of the parent row.</param>
    /// <returns>Total number of rows soft-deleted (parent + descendants).</returns>
    public Task<int> UnshareAsync(
        string parentTableName,
        Guid parentId,
        CancellationToken cancellationToken = default)
        => SyncableFkCascade.SoftDeleteSubtreeAsync(
            context, parentTableName, parentId, cancellationToken);
}
