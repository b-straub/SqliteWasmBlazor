// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Runtime.Versioning;
using System.Text.Json;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// Extension methods for database operations with SqliteWasm provider.
/// </summary>
public static class SqliteWasmDatabaseExtensions
{
    /// <summary>
    /// Ensures the database and schema are created only if they don't already exist.
    /// This is the async-compatible alternative to EnsureCreatedAsync that properly
    /// checks for existing tables before attempting creation.
    /// </summary>
    /// <param name="database">The database facade from DbContext.Database</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the database was created, false if it already existed</returns>
    [SupportedOSPlatform("browser")]
    public static async Task<bool> EnsureCreatedIfNeededAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        // Try to query sqlite_master to check if tables exist
        try
        {
            await using var connection = (SqliteWasmConnection)database.GetDbConnection();

            // Ensure connection is open
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";

            var result = await command.ExecuteScalarAsync(cancellationToken);

            // Handle JsonElement from our worker bridge
            int tableCount = result switch
            {
                JsonElement je => je.GetInt32(),
                int i => i,
                long l => (int)l,
                _ => Convert.ToInt32(result)
            };

            if (tableCount == 0)
            {
                // No tables exist, safe to create
                await database.EnsureCreatedAsync(cancellationToken);
                return true;
            }

            // Tables already exist, no need to create
            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("no such table: sqlite_master") ||
                                     ex.Message.Contains("unable to open database"))
        {
            // Database doesn't exist yet or is inaccessible, create it
            await database.EnsureCreatedAsync(cancellationToken);
            return true;
        }
    }
}
