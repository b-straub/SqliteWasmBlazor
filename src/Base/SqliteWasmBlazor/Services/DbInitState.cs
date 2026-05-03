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
    /// The on-disk file is encrypted (slot 0 ChaCha20 ciphertext, no SQLite
    /// magic) but the worker registry has no key registered for it — boot
    /// init can't read schema until the user authenticates and the key is
    /// installed. Distinct from <see cref="FAILED"/> because this is not an
    /// error the user can resolve via reset; the cure is sign-in. UI: alert
    /// stays silent, navigation should land on the encryption page so the
    /// user can authenticate. Promotes to <see cref="READY"/> automatically
    /// when an authenticated session installs K and a successful EF read
    /// confirms the schema.
    /// </summary>
    ENCRYPTED_LOCKED = 7,
}
