// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// Database creator for SqliteWasm provider that uses OPFS storage via worker bridge.
/// Overrides file operations to work with OPFS instead of Emscripten MEMFS.
/// </summary>
internal sealed class SqliteWasmDatabaseCreator : RelationalDatabaseCreator
{
    private readonly IRawSqlCommandBuilder _rawSqlCommandBuilder;

    public SqliteWasmDatabaseCreator(
        RelationalDatabaseCreatorDependencies dependencies,
        IRawSqlCommandBuilder rawSqlCommandBuilder)
        : base(dependencies)
    {
        _rawSqlCommandBuilder = rawSqlCommandBuilder;
    }

    /// <summary>
    /// Gets the database name from the connection string.
    /// </summary>
    private string GetDatabaseName()
    {
        var connectionString = Dependencies.Connection.ConnectionString;

        if (string.IsNullOrEmpty(connectionString))
        {
            return ":memory:";
        }

        var parts = connectionString.Split(';');
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 &&
                kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                return kv[1].Trim();
            }
        }

        return ":memory:";
    }

    /// <summary>
    /// Creates the database by setting WAL (Write-Ahead Logging) mode.
    /// Not supported synchronously in WebAssembly - use CreateAsync instead.
    /// </summary>
    public override void Create()
    {
        throw new NotSupportedException(
            "Synchronous database operations are not supported in WebAssembly. Use CreateAsync instead.");
    }

    /// <summary>
    /// Asynchronously creates the database.
    /// WAL mode and FULL synchronous mode are set automatically by OpenAsync().
    /// </summary>
    public override async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        // OpenAsync() sets PRAGMA journal_mode = WAL and PRAGMA synchronous = FULL
        await Dependencies.Connection.OpenAsync(cancellationToken);
        await Dependencies.Connection.CloseAsync();
    }

    /// <summary>
    /// Checks if the database exists in OPFS storage.
    /// </summary>
    public override bool Exists()
    {
        // In-memory databases always exist
        if (IsInMemoryDatabase())
        {
            return true;
        }

        // Check OPFS storage via worker bridge (synchronous wrapper)
        return ExistsAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously checks if the database exists in OPFS storage.
    /// </summary>
    public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        // In-memory databases always exist
        if (IsInMemoryDatabase())
        {
            return true;
        }

        // Check OPFS storage via worker bridge
        var dbName = GetDatabaseName();
        return await SqliteWasmWorkerBridge.Instance.ExistsDatabaseAsync(dbName, cancellationToken);
    }

    /// <summary>
    /// Checks if any user-defined tables exist in the database.
    /// </summary>
    public override bool HasTables()
    {
        return HasTablesAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously checks if any user-defined tables exist in the database.
    /// </summary>
    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _rawSqlCommandBuilder
            .Build("SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"rootpage\" IS NOT NULL;")
            .ExecuteScalarAsync(
                new RelationalCommandParameterObject(
                    Dependencies.Connection,
                    null,
                    null,
                    Dependencies.CurrentContext.Context,
                    Dependencies.CommandLogger,
                    CommandSource.Migrations),
                cancellationToken);

        // Handle JsonElement from worker bridge
        if (result is Text.Json.JsonElement jsonElement &&
            jsonElement.ValueKind == Text.Json.JsonValueKind.Number)
        {
            return jsonElement.GetInt64() != 0;
        }

        // Fallback for other types
        return Convert.ToInt64(result) != 0;
    }

    /// <summary>
    /// Deletes the database from OPFS storage.
    /// Not supported synchronously in WebAssembly - use DeleteAsync instead.
    /// </summary>
    public override void Delete()
    {
        throw new NotSupportedException(
            "Synchronous database operations are not supported in WebAssembly. Use DeleteAsync instead.");
    }

    /// <summary>
    /// Asynchronously deletes the database from OPFS storage.
    /// </summary>
    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        // Close connection first
        if (Dependencies.Connection.DbConnection.State == ConnectionState.Open)
        {
            await Dependencies.Connection.CloseAsync();
        }

        // Don't delete in-memory databases
        if (IsInMemoryDatabase())
        {
            return;
        }

        // Delete from OPFS via worker bridge
        var dbName = GetDatabaseName();
        await SqliteWasmWorkerBridge.Instance.DeleteDatabaseAsync(dbName, cancellationToken);
    }

    /// <summary>
    /// Checks if the connection is to an in-memory database.
    /// </summary>
    private bool IsInMemoryDatabase()
    {
        var dbName = GetDatabaseName();
        return string.IsNullOrEmpty(dbName) ||
               dbName.Equals(":memory:", StringComparison.OrdinalIgnoreCase);
    }
}
