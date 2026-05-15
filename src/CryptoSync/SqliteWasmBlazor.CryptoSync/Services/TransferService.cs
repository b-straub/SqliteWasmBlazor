using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Moves a <see cref="SyncableEntity"/> subtree from one group to another.
/// Since <see cref="SyncableEntity.SharingId"/> is immutable per row, a
/// "move" is implemented as tombstone-source + clone-to-target with fresh
/// Ids. The interceptor stamps sync metadata on the clones, and the worker
/// encrypts them under the target group's CEK on next export.
///
/// <para>
/// No crypto involvement on the C# side — the transferring device already
/// has plaintext in the open table. The clone is a pure domain-data copy
/// with FK remapping via the generator-emitted
/// <see cref="CryptoSyncContextBase.CloneForTransfer"/>.
/// </para>
/// </summary>
internal class TransferService(CryptoSyncContextBase context)
{
    /// <summary>
    /// Transfer a parent entity and all FK descendants to a new group.
    /// Returns the old→new Id mapping for caller-side reference updates.
    /// </summary>
    /// <param name="parentTableName">Open table name of the parent entity (e.g. "TestLists").</param>
    /// <param name="parentId">Id of the parent row to transfer.</param>
    /// <param name="targetSharingId">SharingId of the target group.</param>
    /// <param name="targetSharingScope">SharingScope for the cloned rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping old Ids to new Ids.</returns>
    public async Task<Dictionary<Guid, Guid>> TransferSubtreeAsync(
        string parentTableName,
        Guid parentId,
        string targetSharingId,
        SharingScope targetSharingScope = SharingScope.SHARED,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(targetSharingId))
        {
            throw new ArgumentException("Target SharingId must not be empty.", nameof(targetSharingId));
        }

        // Collect the full subtree (parent + all FK descendants) as tracked entities.
        var subtree = await CollectSubtreeAsync(parentTableName, parentId, cancellationToken)
            .ConfigureAwait(false);

        if (subtree.Count == 0)
        {
            throw new InvalidOperationException(
                $"TransferService: no row found in '{parentTableName}' with Id {parentId}");
        }

        // Build old→new Id mapping.
        var idMap = new Dictionary<Guid, Guid>();
        foreach (var entity in subtree)
        {
            idMap[entity.Id] = Guid.NewGuid();
        }

        await using var tx = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 1. Tombstone the source subtree.
        var now = DateTime.UtcNow;
        foreach (var entity in subtree)
        {
            entity.IsDeleted = true;
            entity.DeletedAt = now;
            entity.UpdatedAt = now;
        }

        // 2. Clone each entity with remapped FKs and target group.
        foreach (var source in subtree)
        {
            var clone = context.CloneForTransfer(source, idMap);
            clone.Id = idMap[source.Id];
            clone.SharingId = targetSharingId;
            clone.SharingScope = targetSharingScope;
            context.Add(clone);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return idMap;
    }

    /// <summary>
    /// Walk the FK tree from the parent row downward, collecting all entities
    /// as tracked instances. Uses the generator-emitted FK map and raw SQL
    /// for Id discovery, then loads each entity via <c>FindAsync</c>.
    /// </summary>
    private async Task<List<SyncableEntity>> CollectSubtreeAsync(
        string parentTableName,
        Guid parentId,
        CancellationToken cancellationToken)
    {
        var result = new List<SyncableEntity>();
        var visited = new HashSet<(string Table, Guid Id)>();

        await WalkAsync(parentTableName, parentId).ConfigureAwait(false);
        return result;

        async Task WalkAsync(string tableName, Guid rowId)
        {
            if (!visited.Add((tableName, rowId)))
            {
                return;
            }

            // Find the CLR type for this table so we can use FindAsync.
            var entityType = FindEntityTypeByTable(tableName)
                ?? throw new InvalidOperationException(
                    $"TransferService: no entity type mapped to table '{tableName}'");

            var entity = await context.FindAsync(entityType.ClrType, [rowId], cancellationToken)
                .ConfigureAwait(false);

            if (entity is not SyncableEntity syncable || syncable.IsDeleted)
            {
                return;
            }

            result.Add(syncable);

            // Walk children via generated FK map.
            var children = context.GetChildFkRelations(tableName);
            foreach (var child in children)
            {
#pragma warning disable EF1002
                var childIds = await context.Database
                    .SqlQueryRaw<Guid>(
                        $"SELECT \"Id\" AS \"Value\" FROM \"{child.ChildTable}\" WHERE \"{child.FkColumn}\" = {{0}} AND \"IsDeleted\" = 0",
                        rowId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore EF1002

                foreach (var childId in childIds)
                {
                    await WalkAsync(child.ChildTable, childId).ConfigureAwait(false);
                }
            }
        }
    }

    private Microsoft.EntityFrameworkCore.Metadata.IEntityType? FindEntityTypeByTable(string tableName)
    {
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (string.Equals(entityType.GetTableName(), tableName, StringComparison.Ordinal))
            {
                return entityType;
            }
        }
        return null;
    }
}
