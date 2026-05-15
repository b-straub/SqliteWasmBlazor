using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Shared FK-walk helper for soft-deleting an entire <see cref="SyncableEntity"/>
/// subtree. Uses <see cref="CryptoSyncContextBase.GetChildFkRelations"/>
/// (generator-emitted) for compile-time FK discovery — AOT/trim-safe.
/// </summary>
public static class SyncableFkCascade
{
    public static async Task<int> SoftDeleteSubtreeAsync(
        CryptoSyncContextBase context,
        string parentTableName,
        Guid parentId,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<(string Table, Guid Id)>();
        var now = DateTime.UtcNow;
        var rowsAffected = 0;

        await WalkAsync(parentTableName, parentId).ConfigureAwait(false);
        return rowsAffected;

        async Task WalkAsync(string tableName, Guid rowId)
        {
            if (!visited.Add((tableName, rowId)))
            {
                return;
            }

#pragma warning disable EF1002
            var affected = await context.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{tableName}\" SET \"IsDeleted\" = 1, \"DeletedAt\" = {{0}}, \"UpdatedAt\" = {{1}} WHERE \"Id\" = {{2}} AND \"IsDeleted\" = 0",
                [now, now, rowId],
                cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
            rowsAffected += affected;

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
}
