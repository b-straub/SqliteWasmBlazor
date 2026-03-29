// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Service for managing SQLite databases in OPFS (Origin Private File System).
/// Provides operations for checking existence, deleting, renaming, and closing databases.
/// </summary>
public interface ISqliteWasmDatabaseService
{
    /// <summary>
    /// Checks if a database exists in OPFS.
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the database exists, false otherwise</returns>
    Task<bool> ExistsDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a database from OPFS.
    /// </summary>
    /// <param name="databaseName">The database filename to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a database in OPFS.
    /// </summary>
    /// <param name="oldName">The current database filename</param>
    /// <param name="newName">The new database filename</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RenameDatabaseAsync(string oldName, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a database connection in the worker.
    /// Note: This closes the worker-side connection, not the C# DbConnection.
    /// </summary>
    /// <param name="databaseName">The database filename to close</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CloseDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a raw .db file into OPFS.
    /// The database is not opened after import - caller must re-open when ready
    /// (e.g., after cleaning up backup files to avoid SAH pool exhaustion).
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db")</param>
    /// <param name="data">Raw SQLite database bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ImportDatabaseAsync(string databaseName, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a raw .db file from OPFS.
    /// The database is closed before export for a consistent snapshot.
    /// Caller must re-open the database after export.
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Raw SQLite database bytes</returns>
    Task<byte[]> ExportDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk import: sends V2 MessagePack payload (header + items) to the worker
    /// for direct insertion using a prepared statement loop.
    /// The worker handles SQL construction, type conversions, and transactions.
    /// </summary>
    /// <param name="databaseName">Target database filename</param>
    /// <param name="payload">V2 MessagePack bytes (header + serialized items)</param>
    /// <param name="conflictStrategy">Conflict resolution strategy for UPSERT behavior</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of rows inserted</returns>
    Task<int> BulkImportAsync(string databaseName, byte[] payload, ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.None, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk export: worker queries SQLite directly and returns V2 MessagePack bytes (header + rows).
    /// C# receives raw bytes for file download without per-item processing.
    /// </summary>
    /// <param name="databaseName">Source database filename</param>
    /// <param name="exportMetadata">Export parameters (tableName, columns, where, orderBy, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>V2 MessagePack bytes (header + serialized rows)</returns>
    Task<byte[]> BulkExportAsync(string databaseName, object exportMetadata, CancellationToken cancellationToken = default);
}
