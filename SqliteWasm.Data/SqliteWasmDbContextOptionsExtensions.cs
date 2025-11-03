// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// Extension methods for configuring SqliteWasm provider with EF Core.
/// </summary>
public static class SqliteWasmDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the DbContext to use the SqliteWasm provider with the specified connection.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context</param>
    /// <param name="connection">The SqliteWasmConnection to use</param>
    /// <returns>The options builder for chaining</returns>
    public static DbContextOptionsBuilder UseSqliteWasm(
        this DbContextOptionsBuilder optionsBuilder,
        SqliteWasmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        // Use the standard EF Core Sqlite provider with our custom connection
        // The connection handles all the worker bridge communication
        return optionsBuilder.UseSqlite(connection);
    }

    /// <summary>
    /// Configures the DbContext to use the SqliteWasm provider with the specified connection string.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context</param>
    /// <param name="connectionString">The connection string (e.g., "Data Source=MyDb.db")</param>
    /// <returns>The options builder for chaining</returns>
    public static DbContextOptionsBuilder UseSqliteWasm(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));
        }

        var connection = new SqliteWasmConnection(connectionString);
        return optionsBuilder.UseSqliteWasm(connection);
    }
}
