// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SqliteWasmBlazor;

/// <summary>
/// Extension methods for configuring SqliteWasm services.
/// </summary>
public static class SqliteWasmServiceCollectionExtensions
{
    /// <summary>
    /// Registers SqliteWasm services including the database management service.
    /// Call this in Program.cs before building the app.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqliteWasm(this IServiceCollection services)
    {
        services.AddSingleton<ISqliteWasmDatabaseService>(SqliteWasmWorkerBridge.Instance);
        return services;
    }

    /// <summary>
    /// Initializes the SqliteWasm worker bridge without Entity Framework Core.
    /// Use this method when using the ADO.NET provider directly without EF Core.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when initialization fails or database is locked by another tab.</exception>
    public static async Task InitializeSqliteWasmAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Initialize the worker bridge
            await SqliteWasmWorkerBridge.Instance.InitializeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
$"""
{ex.Message}
Database is locked by another browser tab.
This application uses OPFS (Origin Private File System) which only allows one tab to access the database at a time.
Please close any other tabs running this application and refresh the page.
""", ex);
        }
    }

    /// <summary>
    /// Initializes the SqliteWasm database by applying pending migrations and handling migration history recovery.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type to initialize.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static async Task InitializeSqliteWasmDatabaseAsync<TContext>(
        this IServiceProvider services)
        where TContext : DbContext
    {
        var initService = services.GetRequiredService<IDBInitializationService>();

        try
        {
            // Initialize the worker bridge first
            await SqliteWasmWorkerBridge.Instance.InitializeAsync();
        }
        catch (Exception ex)
        {
            initService.ErrorMessage =
$"""
{ex.Message}
Database is locked by another browser tab.
This application uses OPFS (Origin Private File System) which only allows one tab to access the database at a time.
Please close any other tabs running this application and refresh the page.
""";
            return;
        }

        try
        {
            // Create a scope for database initialization
            using var scope = services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
            await using var dbContext = await factory.CreateDbContextAsync();

            // Apply pending migrations only (skip if database is already up to date)
            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
            {
                try
                {
                    await dbContext.Database.MigrateAsync();
                }
                catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                                            (ex.Message.Contains("table", StringComparison.OrdinalIgnoreCase) &&
                                             ex.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)))
                {
                    // Tables exist but migration history is missing/corrupted
                    // This can happen if database was created with EnsureCreated or __EFMigrationsHistory was deleted
                    // Attempt to recover by recreating the migration history table
                    var recovered = await RecoverMigrationHistoryAsync(dbContext);

                    if (!recovered)
                    {
                        initService.ErrorMessage =
"""
Database schema is incompatible with the current version.
This can happen after updating the application or testing with different database formats.

Click the "Reset Database" button below to delete the incompatible database and recreate it with the correct schema.
""";
                    }
                }
            }
        }
        catch (TimeoutException)
        {
            initService.ErrorMessage =
"""
Database is locked by another browser tab.
This application uses OPFS (Origin Private File System) which only allows one tab to access the database at a time.
Please close any other tabs running this application and refresh the page.
""";
        }
        catch (Exception ex)
        {
            initService.ErrorMessage = $"ERROR initializing database: {ex.GetType().Name}: {ex.Message}";
            initService.ErrorMessage += Environment.NewLine;
            initService.ErrorMessage += $"{ex.StackTrace}";
        }
    }

    /// <summary>
    /// Recovers the migration history table when it's missing or corrupted.
    /// Verifies the schema matches expectations after recovery.
    /// </summary>
    /// <returns>True if recovery succeeded and schema is valid, false otherwise.</returns>
    private static async Task<bool> RecoverMigrationHistoryAsync(DbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();

        try
        {
            await connection.OpenAsync();

            // Create migrations history table if missing
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                    MigrationId TEXT NOT NULL PRIMARY KEY,
                    ProductVersion TEXT NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync();

            // Mark all migrations as applied (assumes current schema matches latest migration)
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

            // Verify the schema is actually compatible
            // Note: GetPendingMigrationsAsync will return empty since we just marked them all as applied,
            // so we need to actually test if the schema works

            // Check if tables exist
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name != '__EFMigrationsHistory';";
            cmd.Parameters.Clear();
            var tableCount = await cmd.ExecuteScalarAsync();

            if (tableCount is null || Convert.ToInt64(tableCount) == 0)
            {
                // No tables exist - schema is wrong
                return false;
            }

            // Try to actually use the DbContext to verify the schema matches
            // This will throw if column names/types don't match
            var modelEntityTypes = dbContext.Model.GetEntityTypes();
            foreach (var entityType in modelEntityTypes)
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName))
                {
                    continue;
                }

                // Query with SELECT * to force column mapping validation
                // This will fail if column names or types don't match the model
                cmd.CommandText = $"SELECT * FROM \"{tableName}\" LIMIT 0";
                cmd.Parameters.Clear();

                // ExecuteReader will force schema validation
                await using var reader = await cmd.ExecuteReaderAsync();

                // Get expected columns from the EF Core model (exclude shadow properties)
                // Only include properties that map to database columns
                var expectedColumns = entityType.GetProperties()
                    .Where(p => !p.IsShadowProperty())
                    .Select(p => p.GetColumnName())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Get actual columns from the database
                var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    actualColumns.Add(reader.GetName(i));
                }

                // Check if all expected columns exist in the database
                foreach (var expectedColumn in expectedColumns)
                {
                    if (!actualColumns.Contains(expectedColumn))
                    {
                        // Missing column - schema is incompatible (e.g., missing "CreatedAt")
                        return false;
                    }
                }

                // Check if column counts match (catches extra columns in DB)
                if (actualColumns.Count != expectedColumns.Count)
                {
                    // Column count mismatch - schema is incompatible
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // Recovery or verification failed
            return false;
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
