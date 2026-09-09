using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SqliteWasmBlazor;

/// <summary>
/// Rebuilds <c>__EFMigrationsHistory</c> when a database carries the schema a
/// migration produces but not the row saying so — the state a tab closed
/// mid-upgrade leaves behind.
/// </summary>
internal static class MigrationHistoryRecovery
{
    /// <summary>
    /// Outcome of <see cref="RunAsync"/>: whether recovery
    /// landed on a usable schema, and any per-column mismatches detected
    /// during verification.
    /// </summary>
    internal sealed record RecoveryResult(bool Succeeded, IReadOnlyList<SchemaMismatch> Mismatches);

    /// <summary>
    /// Recovers the migration history table when it's missing or corrupted.
    /// Walks every entity in the EF model and verifies its mapped columns
    /// exist in the live SQLite schema. Returns structured per-column
    /// diagnostics so callers can render actionable UI rather than a string.
    /// </summary>
    internal static async Task<RecoveryResult> RunAsync(DbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        var mismatches = new List<SchemaMismatch>();

        try
        {
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                    MigrationId TEXT NOT NULL PRIMARY KEY,
                    ProductVersion TEXT NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync();

            var allMigrations = dbContext.Database.GetMigrations();
            foreach (var migration in allMigrations)
            {
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                    VALUES ($migration, $version);";
                cmd.Parameters.Clear();

                var migrationParam = cmd.CreateParameter();
                migrationParam.ParameterName = "$migration";
                migrationParam.Value = migration;
                cmd.Parameters.Add(migrationParam);

                var versionParam = cmd.CreateParameter();
                versionParam.ParameterName = "$version";
                versionParam.Value = "10.0.0";
                cmd.Parameters.Add(versionParam);

                await cmd.ExecuteNonQueryAsync();
            }

            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name != '__EFMigrationsHistory';";
            cmd.Parameters.Clear();
            var tableCount = await cmd.ExecuteScalarAsync();

            if (tableCount is null || Convert.ToInt64(tableCount) == 0)
            {
                return new RecoveryResult(false, mismatches);
            }

            // Use the design-time model so IsTableExcludedFromMigrations is
            // available — the runtime model strips that annotation. Mirrors
            // ValidateImportedSchemaAsync's filter so FTS5 / virtual tables
            // marked ExcludeFromMigrations don't produce spurious mismatches.
            var designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
            foreach (var entityType in designTimeModel.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName)
                    || entityType.IsOwned()
                    || entityType.IsTableExcludedFromMigrations())
                {
                    continue;
                }

                // PRAGMA table_info is the SQLite-canonical introspection
                // path. SELECT * LIMIT 0 is unreliable here — some drivers
                // (this one included) only populate column metadata when at
                // least one row is materialized, leaving FieldCount=0 on
                // empty results and miscounting every column as missing.
                cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
                cmd.Parameters.Clear();

                var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    // table_info row shape: cid, name, type, notnull, dflt_value, pk
                    while (await reader.ReadAsync())
                    {
                        actualColumns.Add(reader.GetString(1));
                    }
                }

                var expectedColumns = entityType.GetProperties()
                    .Where(p => !p.IsShadowProperty())
                    .Select(p => p.GetColumnName())
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Select(c => c)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var expectedColumn in expectedColumns)
                {
                    if (!actualColumns.Contains(expectedColumn))
                    {
                        mismatches.Add(new SchemaMismatch(tableName, expectedColumn, null));
                    }
                }

                foreach (var actualColumn in actualColumns)
                {
                    if (!expectedColumns.Contains(actualColumn))
                    {
                        mismatches.Add(new SchemaMismatch(tableName, null, actualColumn));
                    }
                }
            }

            return new RecoveryResult(mismatches.Count == 0, mismatches);
        }
        catch
        {
            return new RecoveryResult(false, mismatches);
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }
}
