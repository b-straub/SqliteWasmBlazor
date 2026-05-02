// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Outcome returned by <see cref="ISqliteWasmDatabaseService.InstallEncryptionKeyAsync"/>.
/// The worker stores the key, then — if a DB file already exists at the path —
/// AEAD-tests slot 0 against the key. On <see cref="WRONG_KEY"/> the worker
/// drops the registry entry before returning, so the registry never carries
/// a known-wrong key.
/// </summary>
public enum VfsKeyInstallResult
{
    /// <summary>
    /// No DB file exists at this path. The key is registered and the next
    /// write through the encrypted VFS will materialize a fresh DB.
    /// </summary>
    NO_EXISTING_DB = 0,

    /// <summary>
    /// A DB file exists and slot 0 decrypts cleanly — key verified.
    /// </summary>
    MATCH = 1,

    /// <summary>
    /// A DB file exists but slot 0 failed AEAD authentication — the key
    /// does not match what encrypted this DB. The worker has cleared the
    /// registry entry; caller must re-install with a different key or wipe.
    /// </summary>
    WRONG_KEY = 2,
}

/// <summary>
/// Selects the slot-rekey flavour applied by
/// <see cref="ISqliteWasmDatabaseService.ExportDatabaseAsync"/>. The worker
/// always closes the DB for a consistent snapshot; the mode controls whether
/// the bytes returned are passed through verbatim, decrypted under the
/// registered source key, or re-wrapped under a caller-supplied key.
/// </summary>
public enum VfsExportMode
{
    /// <summary>
    /// Verbatim raw bytes from OPFS. Plain DBs return SQLite pages,
    /// PRF-VFS-encrypted DBs return slot-format ciphertext (4124-byte slots)
    /// under the currently registered key.
    /// </summary>
    VERBATIM = 0,

    /// <summary>
    /// Decrypt every slot under the registered source key and return plain
    /// SQLite pages a standard SQLite implementation can open. If no key is
    /// registered, the result is identical to <see cref="VERBATIM"/>.
    /// </summary>
    PLAIN = 1,

    /// <summary>
    /// Decrypt every slot under the registered source key (if any) and
    /// re-encrypt under a caller-supplied 32-byte ChaCha20-Poly1305 key with
    /// the same path-bound AAD. Requires the <c>newKey</c> argument to be
    /// exactly 32 bytes; AAD binds <c>dbPath</c> so the recipient must
    /// import to the same database name. Source MUST be encrypted (a key
    /// must be registered for this path); use <see cref="ENCRYPT"/> for the
    /// plain-source case.
    /// </summary>
    REKEY = 2,

    /// <summary>
    /// Encrypt a plain database under a caller-supplied 32-byte
    /// ChaCha20-Poly1305 key with the path-bound AAD; symmetric with
    /// <see cref="REKEY"/> but for the byte-shuttle backup / sharing case
    /// where the source has no registered key. Source MUST be plain (no
    /// key registered for this path); the worker rejects otherwise. The
    /// recipient imports the bytes via <see cref="ISqliteWasmDatabaseService.ImportDatabaseAsync"/>
    /// after registering the same K_target.
    /// </summary>
    ENCRYPT = 3,
}

/// <summary>
/// Outcome returned by <see cref="ISqliteWasmDatabaseService.ImportDatabaseAsync"/>.
/// Plain (non-opaque) imports always return <see cref="OK"/> on success and
/// throw on byte-level failures. Opaque (encrypted) imports go through the
/// refuse-on-existing + verify-on-write policy: a fresh-path import that
/// AEAD-verifies under the registered key returns <see cref="OK"/>; an
/// import refused because a DB already exists at the path returns
/// <see cref="EXISTING_DB_REFUSED"/>; an import whose slot 0 fails AEAD
/// under the registered key returns <see cref="WRONG_KEY"/> after the
/// worker has rolled back (unlinked) the partial file.
/// </summary>
public enum VfsImportResult
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
    /// Imports a raw .db file into OPFS. The database is not opened after
    /// import — caller must re-open when ready (e.g., after cleaning up
    /// backup files to avoid SAH pool exhaustion).
    ///
    /// Auto-detects ciphertext vs plaintext via the SQLite-format-3 magic
    /// bytes. Plain imports allow overwriting an existing DB and always
    /// return <see cref="VfsImportResult.OK"/> on success. Opaque (encrypted)
    /// imports are subject to the refuse-on-existing + verify-on-write
    /// policy and may return <see cref="VfsImportResult.EXISTING_DB_REFUSED"/>
    /// or <see cref="VfsImportResult.WRONG_KEY"/>.
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db")</param>
    /// <param name="data">Raw SQLite database bytes (plaintext .db file or
    /// PRF-VFS slot-format ciphertext)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<VfsImportResult> ImportDatabaseAsync(string databaseName, byte[] data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a raw .db file from OPFS in one of three slot-rekey flavours.
    /// The worker always closes the DB before exporting for a consistent
    /// snapshot — caller must re-open afterwards.
    ///
    /// Flavour is selected via <paramref name="mode"/>:
    /// <list type="bullet">
    /// <item><description><see cref="VfsExportMode.VERBATIM"/> — raw OPFS
    /// bytes; plain pages for plain DBs, slot-format ciphertext for
    /// encrypted DBs.</description></item>
    /// <item><description><see cref="VfsExportMode.PLAIN"/> — decrypts every
    /// slot under the registered key and returns plain SQLite pages. No-op
    /// for plain DBs.</description></item>
    /// <item><description><see cref="VfsExportMode.REKEY"/> — decrypts under
    /// the registered source key (if any) and re-encrypts under
    /// <paramref name="newKey"/> with the same path-bound AAD. The resulting
    /// bytes can be handed to <see cref="ImportDatabaseAsync"/> on the
    /// recipient side, where they verify-on-write under the same key
    /// registered via <see cref="InstallEncryptionKeyAsync"/>.</description></item>
    /// </list>
    ///
    /// AAD constraint (REKEY): the recipient must import to the same database
    /// name the sender exported from — AAD binds <c>dbPath</c>. Cross-path
    /// migration is not supported by this primitive.
    ///
    /// REKEY transport: <paramref name="newKey"/> is consumed synchronously;
    /// bytes are copied into a MessagePack envelope, posted to the worker,
    /// and the envelope buffer is zeroed before this method returns. Callers
    /// should source the memory from <c>SecureKeyCache.UseKey</c> so the key
    /// never crosses an async boundary as a managed <c>byte[]</c>.
    /// </summary>
    /// <param name="databaseName">The database filename (e.g., "mydb.db").</param>
    /// <param name="mode">Slot-rekey flavour. Defaults to <see cref="VfsExportMode.VERBATIM"/>.</param>
    /// <param name="newKey">For <see cref="VfsExportMode.REKEY"/> and
    /// <see cref="VfsExportMode.ENCRYPT"/>: exactly 32 bytes of new key
    /// material. Must be empty for the other modes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw bytes in the format selected by <paramref name="mode"/>.</returns>
    Task<byte[]> ExportDatabaseAsync(string databaseName,
        VfsExportMode mode = VfsExportMode.VERBATIM,
        ReadOnlyMemory<byte> newKey = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// In-place plain → encrypted transition. Reads the OPFS file as plain
    /// SQLite pages, re-wraps every page under the caller-supplied 32-byte
    /// ChaCha20-Poly1305 key with the path-bound AAD, and writes the
    /// encrypted slots back to the same OPFS path. Bytes never cross the
    /// C#↔JS boundary — symmetric to <see cref="ExportDatabaseAsync"/>
    /// with <see cref="VfsExportMode.REKEY"/> but local-only, with no
    /// returned envelope.
    ///
    /// <para>
    /// Caller responsibility: no key may be registered for this path before
    /// the call (the worker rejects otherwise — call
    /// <see cref="ClearEncryptionKeyAsync"/> first if needed) and the caller
    /// must <see cref="InstallEncryptionKeyAsync"/> with the same key
    /// afterwards before opening — the worker's registry is cleared by the
    /// implicit close inside this method.
    /// </para>
    /// </summary>
    /// <param name="databaseName">The database filename to encrypt in place.</param>
    /// <param name="key">Exactly 32 bytes of new key material.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EncryptDatabaseInPlaceAsync(
        string databaseName,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// In-place encrypted → plain transition. Reads the OPFS file under the
    /// currently registered key, decrypts every slot to plain SQLite pages,
    /// and writes the plain pages back to the same OPFS path. Bytes never
    /// cross the C#↔JS boundary; the caller should
    /// <see cref="ClearEncryptionKeyAsync"/> if any state remains (the
    /// worker's registry is cleared by the implicit close inside this
    /// method).
    ///
    /// <para>
    /// Caller responsibility: a key must be registered for this path
    /// before the call (the worker rejects otherwise — typical sequence is
    /// <see cref="InstallEncryptionKeyAsync"/> with K_old, then
    /// <c>DecryptDatabaseInPlaceAsync</c>).
    /// </para>
    /// </summary>
    /// <param name="databaseName">The database filename to decrypt in place.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DecryptDatabaseInPlaceAsync(
        string databaseName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Plain (non-encrypted) row import from a V2 MessagePack payload built
    /// via <c>MessagePackFileHeaderV2</c>. Worker streams rows into the
    /// named target table using a single prepared INSERT inside a
    /// transaction.
    ///
    /// DB-agnostic: column metadata (name, SQL type, C# type) is read from
    /// the payload header itself — no dependency on a CryptoSync
    /// <c>_column_registry</c>. Suitable for plain SQLite databases
    /// (test-data generation, seeding) as well as CryptoSync-bootstrapped
    /// DBs.
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="data">V2 MessagePack bytes: header + row arrays.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows imported.</returns>
    Task<int> ImportRowsAsync(string databaseName, byte[] data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V2 encrypted bulk export — shadow rows as wire format. Worker derives CEK
    /// via crypto-core (ECDH + HKDF), encrypts per-row with AAD (Layer 1 tamper
    /// detection), signs per-row (Layer 2), upserts shadow, returns
    /// MessagePack-packed ShadowRowGroup.
    /// </summary>
    Task<byte[]> DeltaExportAsync(string databaseName, BulkExportMetadata exportMetadata,
        byte[] headerBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// V2 encrypted bulk import with three-layer tamper detection. Consumes a
    /// MessagePack-packed <c>DeltaEnvelope</c> (multi-group, multi-table).
    /// Worker verifies outer Ed25519 envelope signature, staggers groups so
    /// system tables (Contacts/ShareGroups/ShareTargets) land first, then for
    /// each group: verifies the batch signature (Layer 2), unwraps CEK (Layer 3),
    /// decrypts with AAD (Layer 1), enforces permissions, applies to shadow +
    /// open tables. Returns MessagePack-packed aggregated ImportReport.
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="headerBytes">MessagePack-serialized V2CryptoHeader.</param>
    /// <param name="envelopeBytes">MessagePack-packed DeltaEnvelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MessagePack-packed ImportReport bytes.</returns>
    Task<byte[]> DeltaImportAsync(string databaseName, byte[] headerBytes,
        byte[] envelopeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-encrypt every shadow row sharing the given <paramref name="sharingId"/>
    /// under a new content key, in place, inside a single SQLite transaction.
    /// The worker walks every <c>_crypto_*</c> shadow table and re-encrypts
    /// matching rows across all of them — so a sharing group whose descendants
    /// span multiple tables (e.g. List + Items) rotates atomically.
    /// Unwraps old + new CEKs from two V2CryptoHeaders inside the worker —
    /// raw key material never leaves the worker.
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="oldHeaderBytes">MessagePack-serialized V2CryptoHeader for the old key version.</param>
    /// <param name="newHeaderBytes">MessagePack-serialized V2CryptoHeader for the new key version.</param>
    /// <param name="sharingId">
    /// SharingId of the rows to rotate — every shadow row matching this
    /// value across every table gets re-encrypted with the new CEK.
    /// </param>
    /// <param name="newKeyVersion">Optional new key version to stamp on rotated rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of shadow rows re-encrypted (across all tables).</returns>
    Task<int> DeltaRotateKeyAsync(string databaseName,
        byte[] oldHeaderBytes, byte[] newHeaderBytes,
        string sharingId, int? newKeyVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a 32-byte ChaCha20-Poly1305 key for the given DB path in
    /// the worker's key registry and immediately AEAD-tests slot 0 against
    /// the key when a DB file already exists at that path. Subsequent
    /// <c>OpenDatabaseAsync(database)</c> calls (without an explicit key)
    /// will pick up the registered key at xOpen time and route through the
    /// encrypted VFS path.
    ///
    /// The span is consumed synchronously: bytes are copied into a
    /// MessagePack envelope, posted to the worker, and the envelope buffer
    /// is zeroed before this method returns. Callers should source the span
    /// from <c>SecureKeyCache.UseKey</c> so the key never crosses an async
    /// boundary as a managed <c>byte[]</c>.
    /// </summary>
    /// <param name="databaseName">Database filename (e.g., "MyDb.db").</param>
    /// <param name="key">Exactly 32 bytes of key material.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="VfsKeyInstallResult.NO_EXISTING_DB"/> for a fresh path,
    /// <see cref="VfsKeyInstallResult.MATCH"/> if the existing DB decrypts
    /// cleanly, or <see cref="VfsKeyInstallResult.WRONG_KEY"/> if AEAD
    /// auth failed (worker has already cleared the registry entry in that case).
    /// </returns>
    Task<VfsKeyInstallResult> InstallEncryptionKeyAsync(string databaseName,
        ReadOnlySpan<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any registered encryption key for the given DB path. Symmetric
    /// to <see cref="InstallEncryptionKeyAsync"/>; called by consumers when
    /// their domain key expires or the user explicitly locks the DB. The
    /// worker zeroes the registry buffer in place.
    /// </summary>
    /// <param name="databaseName">Database filename.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearEncryptionKeyAsync(string databaseName,
        CancellationToken cancellationToken = default);
}
