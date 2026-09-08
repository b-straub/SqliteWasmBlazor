// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Lifecycle of database initialization. Drives boot-status UI: navigation,
/// splash screens, and recovery prompts. Promoted by
/// <see cref="IDbInitializationReporter"/> and observed via
/// <see cref="IDbInitializationStatus"/>.
/// </summary>
public enum DbInitState
{
    /// <summary>No initialization has been attempted yet.</summary>
    NOT_STARTED = 0,

    /// <summary>Initialization is in progress.</summary>
    INITIALIZING = 1,

    /// <summary>All boot stages succeeded; the database is usable.</summary>
    READY = 2,


    /// <summary>OPFS is held by another tab — boot cannot proceed.</summary>
    TAB_LOCKED = 3,

    /// <summary>Local schema does not match the EF model — manual recovery required.</summary>
    SCHEMA_INCOMPATIBLE = 4,

    /// <summary>Worker init exceeded the timeout.</summary>
    TIMEOUT = 5,

    /// <summary>Catch-all for unexpected init failures (see <see cref="IDbInitFailure"/>).</summary>
    FAILED = 6,

    /// <summary>
    /// The encrypted VFS lock marker is present and the worker has no
    /// active global key — boot detected ciphertext on disk before
    /// touching any DB. The cure is <c>IEncryptedSqliteWasmDatabaseService.UnlockAsync</c>
    /// once the user has supplied valid credentials. Maps to
    /// <see cref="EncryptedDatabaseLockedFailure"/>.
    /// </summary>
    ENCRYPTED_LOCKED = 7,

    /// <summary>
    /// Pending migrations are being applied. Reached once the database is
    /// openable — immediately for a plain pool, after unlock for an encrypted
    /// one — which is why it can be shown: by then a UI exists.
    /// </summary>
    /// <remarks>
    /// Indeterminate. A migration is a sequence of DDL statements with no
    /// progress to report from inside, and on a large database it can take
    /// seconds: 20,000 rows measured 37 ms plain and 119 ms encrypted, which
    /// extrapolates to roughly 24 s for an index build over 4M encrypted rows.
    /// </remarks>
    MIGRATING = 8,
}
