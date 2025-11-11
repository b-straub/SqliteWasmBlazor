// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SqliteWasmBlazor;

/// <summary>
/// Extension methods for configuring SqliteWasm services.
/// </summary>
public static class SqliteWasmServiceCollectionExtensions
{
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
                    await RecoverMigrationHistoryAsync(dbContext);
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
    /// </summary>
    private static async Task RecoverMigrationHistoryAsync(DbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
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
                versionParam.Value = "10.0.0-rc.2.25502.107";
                cmd.Parameters.Add(versionParam);

                await cmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
