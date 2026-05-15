// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Internal-only worker-side mode selector for the bridge's per-DB
/// <c>SqliteWasmWorkerBridge.ExportDatabaseAsync(name, mode, newKey, ct)</c>
/// overload. The encrypted-disk service loops over PLAIN / REKEY for its
/// whole-disk envelope; the encrypted-disk in-place transitions use the
/// matching encrypt/decrypt-in-place primitives. Public consumers should
/// reach for <see cref="ISqliteWasmDatabaseService.ExportDatabaseAsync"/>
/// (single-DB native) or
/// <see cref="IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAsync"/>
/// (whole-disk envelope) instead.
/// </summary>
internal enum VfsExportMode
{
    /// <summary>
    /// Verbatim raw bytes from OPFS — what the public single-DB
    /// <see cref="ISqliteWasmDatabaseService.ExportDatabaseAsync"/> uses.
    /// </summary>
    VERBATIM = 0,

    /// <summary>
    /// Decrypt every slot under the registered source key and return plain
    /// SQLite pages. Source MUST be encrypted.
    /// </summary>
    PLAIN = 1,

    /// <summary>
    /// Decrypt under registered source key + re-encrypt under a
    /// caller-supplied 32-byte key with path-bound AAD. Used by the
    /// encrypted-disk service for "share with recipient under K_new".
    /// </summary>
    REKEY = 2,

    /// <summary>
    /// Encrypt a plain database under a caller-supplied 32-byte key. Source
    /// MUST be plain. Mostly historical; the in-place encrypt path is the
    /// production version of this transition.
    /// </summary>
    ENCRYPT = 3,
}

/// <summary>
/// Outcome returned by <see cref="ISqliteWasmDatabaseService.ImportDatabaseAsync"/>
/// and <see cref="ISqliteWasmDatabaseService.ImportAllDatabasesAsync"/>.
/// Plain (non-opaque) imports always return <see cref="OK"/> on success and
/// throw on byte-level failures. Opaque (encrypted) imports go through the
/// refuse-on-existing + verify-on-write policy: a fresh-path import that
/// AEAD-verifies under the registered key returns <see cref="OK"/>; an
/// import refused because a DB already exists at the path returns
/// <see cref="EXISTING_DB_REFUSED"/>; an import whose slot 0 fails AEAD
/// under the registered key returns <see cref="WRONG_KEY"/> after the
/// worker has rolled back (unlinked) the partial file.
/// </summary>
public enum DiskImportResult
{
    /// <summary>
    /// Bytes written. For opaque imports with a registered key, slot 0 also
    /// AEAD-verified.
    /// </summary>
    OK = 0,

    /// <summary>
    /// Opaque import only: slot 0 failed AEAD authentication under the
    /// registered key. The worker has unlinked the half-written file so no
    /// state survives the failed import.
    /// </summary>
    WRONG_KEY = 1,

    /// <summary>
    /// Opaque import only: a DB file already exists at this path. Caller
    /// must call <see cref="ISqliteWasmDatabaseService.DeleteDatabaseAsync"/>
    /// first. Plain imports keep their overwrite semantics and never return
    /// this code.
    /// </summary>
    EXISTING_DB_REFUSED = 2,
}

/// <summary>
/// Plain SQLite database management on OPFS. Single-DB ops (Exists / Delete
/// / Rename / Close / Import / Export native <c>.db</c>), the pool-wide
/// <see cref="ListDatabasesAsync"/>, plain bulk row insert
/// (<see cref="ImportRowsAsync"/>), and batch
/// <see cref="ExportAllDatabasesAsync"/> /
/// <see cref="ImportAllDatabasesAsync"/> via standard ZIP archives.
///
/// <para>
/// <b>Audience.</b> Anyone using SQLite-on-OPFS — encryption-aware apps
/// (which also use <see cref="IEncryptedSqliteWasmDatabaseService"/>) and
/// pure plain apps. Native SQLite interop: per-DB <c>.db</c> bytes from
/// <see cref="ExportDatabaseAsync"/> open in <c>sqlite3</c>; ZIP archives
/// from <see cref="ExportAllDatabasesAsync"/> unzip to a folder of
/// <c>.db</c> files, each interop-friendly.
/// </para>
///
/// <para>
/// <b>What this is NOT.</b> The encryption lifecycle (Enter/Leave/Lock/
/// Unlock/Reset, ExportDisk envelope, ImportDisk envelope) lives on
/// <see cref="IEncryptedSqliteWasmDatabaseService"/>. The CryptoSync
/// delta-bulk surface (DeltaExport/DeltaImport/DeltaRotate) lives on
/// <c>ICryptoSyncDeltaService</c> in the CryptoSync package. Both are
/// separately registered; consumers who don't need them never see them.
/// </para>
///
/// <para>
/// All single-DB operations refuse to write or read while the encrypted
/// disk is locked — they throw <see cref="DiskLockedException"/> via the
/// bridge gate. Wrap DB-touching code in
/// <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c> to avoid that path.
/// </para>
/// </summary>
public interface ISqliteWasmDatabaseService
{
    /// <summary>
    /// Bare main-DB names currently in the SAH pool — no journal / WAL /
    /// SHM siblings. Cheap pool metadata read; safe to call regardless of
    /// disk lock state.
    /// </summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken = default);

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
    /// Imports a raw <c>.db</c> file into OPFS. The database is not opened
    /// after import — caller must re-open when ready (e.g., after cleaning
    /// up backup files to avoid SAH pool exhaustion).
    ///
    /// <para>
    /// Auto-detects ciphertext vs plaintext via the SQLite-format-3 magic
    /// bytes. Plain imports allow overwriting an existing DB and always
    /// return <see cref="DiskImportResult.OK"/> on success. Opaque
    /// (encrypted) imports are subject to the refuse-on-existing +
    /// verify-on-write policy and may return
    /// <see cref="DiskImportResult.EXISTING_DB_REFUSED"/> or
    /// <see cref="DiskImportResult.WRONG_KEY"/>.
    /// </para>
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db")</param>
    /// <param name="data">Raw SQLite database bytes (plaintext .db file or
    /// PRF-VFS slot-format ciphertext)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<DiskImportResult> ImportDatabaseAsync(string databaseName, byte[] data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a single database as raw native SQLite bytes — equivalent
    /// to dumping the on-disk file. Plain DBs return standard SQLite pages
    /// (<c>sqlite3 file.db</c> opens them); encrypted DBs return slot-format
    /// ciphertext under the active globalKey (only re-importable on a disk
    /// holding the same key). The worker closes the DB before exporting
    /// for a consistent snapshot — caller must re-open afterwards.
    ///
    /// <para>
    /// For batch (multi-DB) export, use <see cref="ExportAllDatabasesAsync"/>
    /// (returns a ZIP archive). For whole-disk encrypted backup / share,
    /// use <see cref="IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw native SQLite bytes (plain pages or slot-format ciphertext).</returns>
    Task<byte[]> ExportDatabaseAsync(string databaseName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Plain (non-encrypted) row import from a V2 MessagePack payload built
    /// via <c>MessagePackFileHeaderV2</c>. Worker streams rows into the
    /// named target table using a single prepared INSERT inside a
    /// transaction.
    ///
    /// <para>
    /// DB-agnostic: column metadata (name, SQL type, C# type) is read from
    /// the payload header itself — no dependency on a CryptoSync
    /// <c>_column_registry</c>. Suitable for plain SQLite databases
    /// (test-data generation, seeding) as well as CryptoSync-bootstrapped
    /// DBs.
    /// </para>
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="data">V2 MessagePack bytes: header + row arrays.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows imported.</returns>
    Task<int> ImportRowsAsync(string databaseName, byte[] data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch export of every database in the SAH pool as a single
    /// <b>ZIP archive</b>. Each entry inside the ZIP is a native
    /// <c>.db</c> file named after the DB (e.g. <c>TodoDb.db</c>) — opens
    /// directly in <c>sqlite3</c> after unzipping. Suitable for
    /// "back up all my plain DBs to one file" workflows.
    ///
    /// <para>
    /// Implementation: loops <see cref="ListDatabasesAsync"/> and calls
    /// <see cref="ExportDatabaseAsync"/> per file, packaging into a
    /// <see cref="System.IO.Compression.ZipArchive"/>. ZIP container is
    /// the standard cross-tool folder representation; no MessagePack /
    /// custom format involved.
    /// </para>
    /// <para>
    /// For whole-disk encrypted backup, use
    /// <see cref="IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAsync"/>
    /// instead — that produces an opaque MessagePack envelope of slot-
    /// format ciphertext, suitable for re-import as an encrypted disk.
    /// </para>
    /// </summary>
    /// <returns>ZIP archive bytes; one entry per database in the pool.</returns>
    Task<byte[]> ExportAllDatabasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch import: replace the entire SAH pool with the contents of the
    /// supplied <b>ZIP archive</b>. Wipes every currently-registered DB
    /// before unpacking, then imports each ZIP entry as a fresh DB named
    /// after the entry. Auto-detects per-entry plain-vs-ciphertext via the
    /// SQLite magic-header probe inside <see cref="ImportDatabaseAsync"/>.
    ///
    /// <para>
    /// <b>Caller is responsible for explicit user confirmation in UI.</b>
    /// The wipe step is destructive and non-recoverable. Per the
    /// disk-as-unit model, partial imports are not supported: either the
    /// ZIP replaces the pool or the call throws (malformed ZIP) / returns
    /// non-OK (per-file failure).
    /// </para>
    /// </summary>
    /// <param name="zipBytes">A ZIP archive previously produced by
    /// <see cref="ExportAllDatabasesAsync"/> (or any ZIP whose entries are
    /// SQLite-format files named after their target DB).</param>
    /// <returns>
    /// <see cref="DiskImportResult.OK"/> on success; the first per-file
    /// non-OK result if any ZIP entry fails to import (e.g.
    /// <see cref="DiskImportResult.WRONG_KEY"/> when ciphertext lands on a
    /// disk holding a different key).
    /// </returns>
    Task<DiskImportResult> ImportAllDatabasesAsync(byte[] zipBytes,
        CancellationToken cancellationToken = default);
}
