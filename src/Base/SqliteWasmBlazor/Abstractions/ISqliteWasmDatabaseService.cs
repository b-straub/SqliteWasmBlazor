// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Outcome of an import that can fail on the key rather than on the bytes —
/// the Crypto plane's guided <c>.eds</c> import, and the raw-slot write the
/// VFS tests use. The streamed imports on this interface do not return it:
/// they signal by exception, because everything they can refuse is a
/// property of the source file rather than a state the caller can act on.
/// </summary>
public enum PoolImportResult
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
/// / Rename / Close), the file paths in and out, the pool-wide
/// <see cref="ListDatabasesAsync"/>, plain bulk row insert
/// (<see cref="ImportRowsAsync"/>).
///
/// <para>
/// <b>Audience.</b> Anyone using SQLite-on-OPFS — encryption-aware apps
/// (which also use <c>IEncryptedSqliteWasmDatabaseService</c>) and
/// pure plain apps.
/// </para>
///
/// <para>
/// <b>Every file path is memory-flat</b> — one database or many, in or out,
/// to a Stream or straight to a download. None of them holds the file in
/// managed memory, so a 250 MB database transfers on a phone. What they
/// carry is a plain <c>.db</c> (or a <c>.dbs</c> envelope of them), which is
/// what <c>sqlite3</c> opens and what the import side reads.
/// </para>
///
/// <para>
/// <b>What this is NOT.</b> The encryption lifecycle (Enter/Leave/Lock/
/// Unlock/Reset, ExportPool envelope, ImportPool envelope) lives on
/// <c>IEncryptedSqliteWasmDatabaseService</c>. The CryptoSync
/// delta-bulk surface (DeltaExport/DeltaImport/DeltaRotate) lives on
/// <c>ICryptoSyncDeltaService</c> in the CryptoSync package. Both are
/// separately registered; consumers who don't need them never see them.
/// </para>
///
/// <para>
/// Every operation refuses to write or read while the encrypted pool is
/// locked. SQL and single-DB ops throw <see cref="PoolLockedException"/> via
/// the bridge gate — that one means consumer code reached the DB outside a
/// <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c> gate. The
/// file-movement paths throw <see cref="PoolOperationRejectedException"/>
/// instead: they are user-driven actions whose remedy is to unlock, and the
/// reason code is what a UI localizes.
/// </para>
/// </summary>
public interface ISqliteWasmDatabaseService
{
    /// <summary>
    /// Whether cancelling a <see cref="CancellationToken"/> stops the
    /// statement the worker is running, rather than only the wait for it.
    ///
    /// <para>
    /// The worker is single-threaded and cannot be told anything while a
    /// statement runs. A cancel reaches it through the service worker that
    /// controls the page: the bridge posts the cancelled request there, and
    /// SQLite's progress handler polls it from inside the statement. That
    /// needs a service worker that has pulled in
    /// <c>_content/SqliteWasmBlazor/sqlite-wasm-cancel.sw.js</c> and
    /// controls the page — which, for the page that registered it, is from
    /// the next navigation on, not the first load.
    /// </para>
    ///
    /// <para>
    /// <c>false</c> means cancellation degrades to what it always was: the
    /// caller's await ends with <see cref="OperationCanceledException"/>, the
    /// worker finishes the statement on its own, and the next request waits
    /// behind it. Decided once when the worker starts and logged at
    /// Information.
    /// </para>
    /// </summary>
    bool CanCancelQueries { get; }

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
    /// Exports a single database into <paramref name="destination"/> without
    /// the bytes ever entering managed memory whole. The worker writes the
    /// export into an OPFS staging file and this drains it a slice at a time,
    /// so the managed peak is one slice (~1 MB) regardless of database size —
    /// materializing is the caller's explicit choice, made by passing a
    /// <see cref="System.IO.MemoryStream"/>.
    ///
    /// <para>
    /// Emits the same bytes as <see cref="ExportDatabaseToDownloadAsync"/>: a
    /// plain <c>.db</c> file, which is what
    /// <see cref="ImportDatabaseFromStreamAsync"/> reads on the other end.
    /// With the Crypto plane loaded the worker is state-aware — a Plain pool
    /// emits verbatim pages, Encrypted+Unlocked decrypts slot-by-slot. The
    /// output is therefore plaintext on an encrypted pool; disclose that
    /// before offering it to a user. The worker closes the database first for
    /// a consistent snapshot — caller must re-open afterwards.
    /// </para>
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db").</param>
    /// <param name="destination">Stream the export is written into. Not closed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PoolOperationRejectedException">
    /// The pool is encrypted and locked
    /// (<see cref="PoolOperationRejection.EXPORT_NEEDS_UNLOCK"/>).
    /// </exception>
    Task ExportDatabaseToStreamAsync(string databaseName, Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a single database straight to a browser download without the
    /// bytes ever entering managed memory. The worker copies the on-disk
    /// file in small slices into an OPFS staging file; the bridge lifts the
    /// finished staging entry as a disk-backed <c>File</c> and fires an
    /// anchor-click download. Memory stays flat regardless of DB size.
    /// Use this whenever the goal is a file the user saves;
    /// <see cref="ExportDatabaseToStreamAsync"/> is the same bytes when the
    /// caller wants them programmatically.
    ///
    /// <para>
    /// With the Crypto plane loaded the worker is state-aware: Plain pools
    /// download verbatim pages; Encrypted+Unlocked pools decrypt slot-by-
    /// slot to plain pages; Encrypted+Locked throws. On the plain plane
    /// the file downloads verbatim. The worker closes the DB before
    /// exporting for a consistent snapshot — caller must re-open afterwards.
    /// </para>
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db").</param>
    /// <param name="filename">Download filename presented to the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PoolOperationRejectedException">
    /// The pool is encrypted and locked
    /// (<see cref="PoolOperationRejection.EXPORT_NEEDS_UNLOCK"/>) — without
    /// the global key the worker cannot turn slots back into plain pages.
    /// </exception>
    Task ExportDatabaseToDownloadAsync(string databaseName, string filename,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports several databases as one <c>.dbs</c> envelope (a MessagePack
    /// array of <c>[name, bytes]</c> tuples, no compression) straight to a
    /// browser download. Memory-flat by the same mechanism as
    /// <see cref="ExportDatabaseToDownloadAsync"/>: the worker assembles the
    /// envelope in an OPFS staging file and the bridge downloads the
    /// disk-backed <c>File</c>.
    ///
    /// <para>
    /// Exactly one name short-circuits to
    /// <see cref="ExportDatabaseToDownloadAsync"/>, so a single-selection
    /// download lands as a vanilla <c>.db</c> file rather than a
    /// one-element envelope. Two or more produce a <c>.dbs</c>.
    /// </para>
    ///
    /// <para>
    /// With the Crypto plane loaded the worker is state-aware: a Plain pool
    /// emits verbatim pages, Encrypted+Unlocked decrypts each file
    /// slot-by-slot to plain pages. Either way the output is plaintext —
    /// disclose that before offering it on an encrypted pool.
    /// </para>
    /// </summary>
    /// <param name="databaseNames">Databases to include; must be non-empty.</param>
    /// <param name="filename">Download filename presented to the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PoolOperationRejectedException">
    /// The pool is encrypted and locked
    /// (<see cref="PoolOperationRejection.EXPORT_NEEDS_UNLOCK"/>).
    /// </exception>
    Task ExportDatabasesToDownloadAsync(IReadOnlyList<string> databaseNames,
        string filename, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports one raw <c>.db</c> file from <paramref name="stream"/> — the
    /// right primitive for "I have one big database file and want it in this
    /// pool". Managed-heap peak is one ArrayPool chunk (~1 MB) whatever the
    /// file's size, and so is every other heap on the way: the chunks are
    /// pushed into the worker one at a time and written into a temp SAH slot
    /// that an atomic replace promotes at the end. Nothing assembles the
    /// file.
    ///
    /// <para>
    /// With the Crypto plane loaded the worker is state-aware: a Plain pool
    /// gets plain pages, Encrypted+Unlocked rekeys-on-write into encrypted
    /// slots.
    /// </para>
    ///
    /// <para>
    /// <paramref name="validateImported"/> turns this into a validated
    /// import: the previous content is parked under
    /// <see cref="PoolNaming.ImportParkSuffix"/>, the file lands under
    /// <paramref name="databaseName"/>, and the delegate gets to open it and
    /// decide. A delegate that throws puts the parked content back exactly
    /// as it was — the replaces are metadata-only, so nothing is rewritten.
    /// </para>
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="stream">Source of the plain <c>.db</c> bytes.</param>
    /// <param name="size">Declared source length; the import fails if the
    /// stream ends early.</param>
    /// <param name="validateImported">Called once with
    /// <paramref name="databaseName"/> after the import commits; throwing
    /// rolls it back. <c>null</c> imports without the park/restore pass.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PoolOperationRejectedException">
    /// The pool is encrypted and locked
    /// (<see cref="PoolOperationRejection.PLAIN_IMPORT_NEEDS_UNLOCK"/>).
    /// </exception>
    Task ImportDatabaseFromStreamAsync(string databaseName, Stream stream, long size,
        Func<string, CancellationToken, ValueTask>? validateImported = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a <c>.dbs</c> envelope from <paramref name="envelopeStream"/>
    /// and writes every entry in it through the same chunked path
    /// <see cref="ImportDatabaseFromStreamAsync"/> uses. The pool ends up
    /// holding exactly what the envelope carries.
    ///
    /// <para>
    /// The worker validates the whole envelope read-only — file count, tuple
    /// arity, page-aligned lengths, SQLite magic per file, no premature EOF —
    /// before any destructive pool operation, so a truncated or crafted
    /// <c>.dbs</c> fails with the existing pool intact.
    /// </para>
    ///
    /// <para>
    /// <paramref name="validateImported"/> makes it a validated import: the
    /// previous content is parked, the envelope's entries land under their
    /// real names, and the delegate is called once per imported database. A
    /// delegate that throws puts the pool back exactly as it was.
    /// </para>
    /// </summary>
    /// <param name="envelopeStream">Source of the <c>.dbs</c> bytes.</param>
    /// <param name="envelopeSize">Declared envelope length; the import fails
    /// if the stream ends early.</param>
    /// <param name="validateImported">Called once per imported database;
    /// throwing rolls the whole import back. <c>null</c> replaces the pool
    /// without the park/restore pass.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PoolOperationRejectedException">
    /// The pool is encrypted and locked
    /// (<see cref="PoolOperationRejection.PLAIN_IMPORT_NEEDS_UNLOCK"/>).
    /// </exception>
    Task ImportDatabasesFromStreamAsync(Stream envelopeStream, long envelopeSize,
        Func<string, CancellationToken, ValueTask>? validateImported = null,
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
}
