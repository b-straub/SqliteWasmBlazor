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
    /// <param name="readonlyColumns">Optional table→column mapping for readonly enforcement.
    /// When a table has readonly columns, the worker validates in a transaction:
    /// snapshot → apply → check no mutations and no new rows → rollback on violation.
    /// Key = table name, Value = readonly column names for that table.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of rows inserted</returns>
    Task<int> BulkImportAsync(string databaseName, byte[] payload, ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.None,
        Dictionary<string, string[]>? readonlyColumns = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk import from raw MessagePack row data with metadata provided separately.
    /// The payload is a MessagePack-serialized List of row arrays (no V2 header required).
    /// Column metadata is sent to the worker as JSON alongside the binary payload.
    /// This enables callers that already have MessagePack-serialized data (e.g., sync services)
    /// to bypass V2 header construction.
    /// </summary>
    /// <param name="databaseName">Target database filename</param>
    /// <param name="metadata">Table structure metadata (table name, columns, primary key)</param>
    /// <param name="rowData">MessagePack-serialized List of row arrays (msgpack List&lt;TDto&gt;)</param>
    /// <param name="conflictStrategy">Conflict resolution strategy for UPSERT behavior</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of rows inserted</returns>
    Task<int> BulkImportRawAsync(string databaseName, BulkImportMetadata metadata, byte[] rowData,
        ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.None,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk export: worker queries SQLite directly and returns V2 MessagePack bytes (header + rows).
    /// C# receives raw bytes for file download without per-item processing.
    /// </summary>
    /// <param name="databaseName">Source database filename</param>
    /// <param name="exportMetadata">Export parameters (tableName, columns, where, orderBy, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>V2 MessagePack bytes (header + serialized rows)</returns>
    Task<byte[]> BulkExportAsync(string databaseName, BulkExportMetadata exportMetadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypted bulk export: worker exports, encrypts V2 bytes with content key via SubtleCrypto AES-GCM.
    /// Plain V2 bytes never leave the worker. Returns encrypted blob + nonce.
    /// Content key is zeroed in worker after use.
    /// </summary>
    /// <param name="databaseName">Source database filename</param>
    /// <param name="exportMetadata">Export parameters</param>
    /// <param name="contentKey">32-byte AES-GCM content key. Caller MUST zero after this call returns.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>(encryptedBlob, nonce) — nonce is 12 bytes</returns>
    Task<(byte[] EncryptedBlob, byte[] Nonce)> BulkExportEncryptedAsync(string databaseName, BulkExportMetadata exportMetadata,
        byte[] contentKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypted bulk import: worker decrypts with content key, inserts into open table + _crypto_ table.
    /// Content key is zeroed in worker after use.
    /// </summary>
    /// <param name="databaseName">Target database filename</param>
    /// <param name="encryptedPayload">AES-GCM encrypted V2 bytes</param>
    /// <param name="nonce">12-byte AES-GCM nonce</param>
    /// <param name="contentKey">32-byte AES-GCM content key. Caller MUST zero after this call returns.</param>
    /// <param name="conflictStrategy">Conflict resolution for open table</param>
    /// <param name="readonlyColumns">Readonly enforcement for open table</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of rows imported into open table</returns>
    Task<int> BulkImportEncryptedAsync(string databaseName, byte[] encryptedPayload, byte[] nonce,
        byte[] contentKey, ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.None,
        Dictionary<string, string[]>? readonlyColumns = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk re-key rotation: re-encrypts every row in a crypto shadow table
    /// (<c>_crypto_&lt;tableName&gt;</c>) under a new content key, in place, inside a single
    /// SQLite transaction. Runs entirely inside the worker — plaintext and ciphertext never
    /// leave the worker during the loop. This is the hot path for revoke and ownership-transfer
    /// operations (CryptoSync plan decision §17 / Phase J benchmark).
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="tableName">Domain table name (not the crypto shadow table — the worker
    /// resolves <c>_crypto_&lt;tableName&gt;</c> internally).</param>
    /// <param name="oldKey">32-byte AES-GCM content key currently encrypting the shadow rows.
    /// Caller MUST zero after this call returns.</param>
    /// <param name="newKey">32-byte AES-GCM content key to re-encrypt under. Caller MUST zero
    /// after this call returns.</param>
    /// <param name="sharingId">Optional filter: when set, only shadow rows whose
    /// <c>SharingId</c> equals this value are rotated (scopes the revoke to one ShareGroup).
    /// When <c>null</c>, every row in the shadow table is rotated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of shadow rows re-encrypted.</returns>
    Task<int> BulkRotateKeyAsync(string databaseName, string tableName, byte[] oldKey, byte[] newKey,
        string? sharingId = null, CancellationToken cancellationToken = default);
}
