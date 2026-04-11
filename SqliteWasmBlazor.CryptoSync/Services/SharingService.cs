using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Sharing assignment service: applies or reverts a sharing group to a
/// subtree of entities, walking foreign-key relationships downward from a
/// parent row so children automatically follow the parent's share.
///
/// <para>
/// Shape: caller picks a parent entity (e.g. a <c>CryptoTestList</c>) and a
/// target <see cref="ShareGroup"/>, the service rewrites
/// <see cref="SyncableEntity.SharingScope"/> /
/// <see cref="SyncableEntity.SharingId"/> / <see cref="SyncableEntity.UpdatedAt"/>
/// on the parent and every descendant reachable via
/// <see cref="IEntityType.GetReferencingForeignKeys"/>. Subsequent deltas
/// carry the updated rows to peers, and
/// <see cref="ISqliteWasmDatabaseService.BulkRotateKeyAsync"/> (which now
/// scans all <c>_crypto_*</c> tables by SharingId) can re-key the entire
/// subtree in a single call.
/// </para>
///
/// <para>
/// The FK walk uses EF Core's pre-built <see cref="IModel"/> metadata plus
/// raw SQL updates — no runtime <c>System.Reflection</c>, no dynamic
/// <c>DbSet&lt;&gt;</c> lookups — so it stays AOT/trim-safe on WASM.
/// </para>
///
/// <para>
/// <b>Scope:</b> this service only rewrites the data-graph assignment. It
/// does <em>not</em> create the target <see cref="ShareGroup"/> or add
/// members — that's <see cref="GroupService"/>'s job. The caller typically
/// composes the two: <c>GroupService.CreateGroupAsync</c> +
/// <c>GroupService.AddMembersAsync</c> + <c>SharingService.ShareAsync</c>.
/// </para>
/// </summary>
public class SharingService(CryptoSyncContextBase context)
{
    /// <summary>
    /// Apply a sharing assignment to a parent entity and every descendant
    /// reachable via foreign keys. Updates
    /// <see cref="SyncableEntity.SharingScope"/> =
    /// <see cref="SharingScope.Shared"/>,
    /// <see cref="SyncableEntity.SharingId"/> = <paramref name="targetGroup"/>'s
    /// SharingId, and bumps <see cref="SyncableEntity.UpdatedAt"/> so the
    /// next sync propagates the change.
    /// </summary>
    /// <param name="parentTableName">Open-table name of the parent entity (e.g. <c>"CryptoTestLists"</c>).</param>
    /// <param name="parentId">Primary key of the parent row.</param>
    /// <param name="targetGroup">The ShareGroup whose SharingId will be stamped on the subtree.</param>
    /// <returns>Total number of rows updated (parent + descendants).</returns>
    public Task<int> ShareAsync(
        string parentTableName,
        Guid parentId,
        ShareGroup targetGroup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetGroup);
        if (string.IsNullOrWhiteSpace(targetGroup.SharingId))
        {
            throw new ArgumentException(
                "SharingService.ShareAsync requires targetGroup.SharingId to be set",
                nameof(targetGroup));
        }

        return ApplyAsync(parentTableName, parentId,
            SharingScope.Shared, targetGroup.SharingId, cancellationToken);
    }

    /// <summary>
    /// Revert a previously shared subtree back to
    /// <see cref="SharingScope.Public"/> + <see cref="CryptoSyncBootstrap.SystemSharingId"/>.
    /// The caller is responsible for rotating the old group's CEK afterward
    /// (via <see cref="ISqliteWasmDatabaseService.BulkRotateKeyAsync"/> on
    /// the old SharingId) if they want to cut off access for departed
    /// members.
    /// </summary>
    public Task<int> UnshareAsync(
        string parentTableName,
        Guid parentId,
        CancellationToken cancellationToken = default)
        => ApplyAsync(parentTableName, parentId,
            SharingScope.Public, CryptoSyncBootstrap.SystemSharingId, cancellationToken);

    private async Task<int> ApplyAsync(
        string parentTableName,
        Guid parentId,
        SharingScope newScope,
        string newSharingId,
        CancellationToken cancellationToken)
    {
        var parentEntityType = FindEntityTypeByTable(parentTableName)
            ?? throw new InvalidOperationException(
                $"SharingService: no entity type mapped to table '{parentTableName}'");

        EnsureSyncableEntity(parentEntityType);

        var visited = new HashSet<(string Table, Guid Id)>();
        var rowsAffected = 0;
        var now = DateTime.UtcNow;
        var newScopeInt = (int)newScope;

        await WalkAsync(parentEntityType, parentId).ConfigureAwait(false);
        return rowsAffected;

        async Task WalkAsync(IEntityType entityType, Guid rowId)
        {
            var table = entityType.GetTableName()
                ?? throw new InvalidOperationException(
                    $"SharingService: entity {entityType.ClrType.Name} has no mapped table");

            if (!visited.Add((table, rowId)))
            {
                return;
            }

            // Update this row first — simple, idempotent, and ensures the
            // UpdatedAt bump covers the whole subtree in one transaction
            // boundary (EF command batching will coalesce these).
            // EF1002 suppressed: `table` comes from EF model metadata, not
            // user input, so interpolation into the identifier slot is safe.
#pragma warning disable EF1002
            var affected = await context.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{table}\" SET \"SharingScope\" = {{0}}, \"SharingId\" = {{1}}, \"UpdatedAt\" = {{2}} WHERE \"Id\" = {{3}}",
                [newScopeInt, newSharingId, now, rowId],
                cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
            rowsAffected += affected;

            // Walk every child entity type that has a FK pointing at this
            // entity type. For each, load the IDs of matching children and
            // recurse. Single-column FKs only — composite keys are out of
            // scope for the current crypto-sync entity shape.
            foreach (var fk in entityType.GetReferencingForeignKeys())
            {
                if (fk.Properties.Count != 1)
                {
                    continue;
                }

                var childType = fk.DeclaringEntityType;
                EnsureSyncableEntity(childType);

                var childTable = childType.GetTableName()
                    ?? throw new InvalidOperationException(
                        $"SharingService: child entity {childType.ClrType.Name} has no mapped table");
                var fkColumn = fk.Properties[0].GetColumnName()
                    ?? throw new InvalidOperationException(
                        $"SharingService: FK {fk.Properties[0].Name} on {childType.ClrType.Name} has no mapped column");

                // EF1002 suppressed: `childTable` and `fkColumn` come from
                // EF model metadata, not user input.
#pragma warning disable EF1002
                var childIds = await context.Database
                    .SqlQueryRaw<Guid>(
                        $"SELECT \"Id\" AS \"Value\" FROM \"{childTable}\" WHERE \"{fkColumn}\" = {{0}}",
                        rowId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore EF1002

                foreach (var childId in childIds)
                {
                    await WalkAsync(childType, childId).ConfigureAwait(false);
                }
            }
        }
    }

    private IEntityType? FindEntityTypeByTable(string tableName)
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

    private static void EnsureSyncableEntity(IEntityType entityType)
    {
        if (!typeof(SyncableEntity).IsAssignableFrom(entityType.ClrType))
        {
            throw new InvalidOperationException(
                $"SharingService: entity {entityType.ClrType.Name} does not inherit SyncableEntity — " +
                "only syncable entities can participate in the share graph");
        }
    }
}
