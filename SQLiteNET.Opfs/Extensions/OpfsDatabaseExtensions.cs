using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Extension methods for configuring SQLite databases for OPFS usage.
/// </summary>
public static class OpfsDatabaseExtensions
{
    /// <summary>
    /// Configures SQLite journal mode for OPFS/WASM compatibility.
    /// This method must be called before EnsureCreatedAsync() or MigrateAsync().
    /// </summary>
    /// <param name="database">The database facade</param>
    /// <returns>The database facade for method chaining</returns>
    /// <exception cref="InvalidOperationException">If called on non-SQLite database or outside browser context</exception>
    public static async Task<DatabaseFacade> ConfigureSqliteForWasmAsync(this DatabaseFacade database)
    {
        if (database.ProviderName is null || !database.ProviderName.EndsWith("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This method can only be called on SQLite databases. " +
                $"Current provider: {database.ProviderName ?? "unknown"}");
        }

        if (!OperatingSystem.IsBrowser())
        {
            // Not in browser - skip configuration (allows same code to work in server-side scenarios)
            return database;
        }

        var connectionString = database.GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Connection string is null or empty");
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // Set journal mode to 'delete' instead of 'wal'
        // Reason: OPFS only syncs the main .db file, not .db-wal and .db-shm files
        // WAL mode is EF Core's default but doesn't work well with MEMFS→OPFS syncing
        command.CommandText = "PRAGMA journal_mode = 'delete';";
        await command.ExecuteNonQueryAsync();

        return database;
    }
}
