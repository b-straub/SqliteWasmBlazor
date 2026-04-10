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
    /// V2 encrypted bulk export — shadow rows as wire format. Worker derives CEK
    /// via crypto-core (ECDH + HKDF), encrypts per-row with AAD (Layer 1 tamper
    /// detection), signs per-row (Layer 2), upserts shadow, returns
    /// MessagePack-packed ShadowRowGroup.
    /// </summary>
    Task<byte[]> BulkExportEncryptedV2Async(string databaseName, BulkExportMetadata exportMetadata,
        byte[] headerBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// V2 encrypted bulk import with three-layer tamper detection. Worker verifies
    /// signatures (Layer 2), unwraps CEK (Layer 3), decrypts with AAD (Layer 1),
    /// applies to open table, returns MessagePack-packed ImportReport.
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="headerBytes">MessagePack-serialized V2CryptoHeader.</param>
    /// <param name="groupBytes">MessagePack-packed ShadowRowGroup to import.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MessagePack-packed ImportReport bytes.</returns>
    Task<byte[]> BulkImportEncryptedV2Async(string databaseName, byte[] headerBytes,
        byte[] groupBytes, CancellationToken cancellationToken = default);

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
