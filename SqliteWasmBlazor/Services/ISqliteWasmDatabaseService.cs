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
    /// Re-encrypt every row in a crypto shadow table under a new content key, in place,
    /// inside a single SQLite transaction. Unwraps old + new CEKs from two V2CryptoHeaders
    /// inside the worker — raw key material never leaves the worker.
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="tableName">Domain table name (worker resolves <c>_crypto_&lt;tableName&gt;</c>).</param>
    /// <param name="oldHeaderBytes">MessagePack-serialized V2CryptoHeader for the old key version.</param>
    /// <param name="newHeaderBytes">MessagePack-serialized V2CryptoHeader for the new key version.</param>
    /// <param name="sharingId">Optional SharingId filter for scoped rotation.</param>
    /// <param name="newKeyVersion">Optional new key version to stamp on rotated rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of shadow rows re-encrypted.</returns>
    Task<int> BulkRotateKeyAsync(string databaseName, string tableName,
        byte[] oldHeaderBytes, byte[] newHeaderBytes,
        string? sharingId = null, int? newKeyVersion = null,
        CancellationToken cancellationToken = default);
}
