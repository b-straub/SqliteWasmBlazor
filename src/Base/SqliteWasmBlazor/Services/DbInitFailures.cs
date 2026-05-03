// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// OPFS is held by another browser tab. Reset does not help — the user must
/// close the other tab and reload. Maps to <see cref="DbInitState.TAB_LOCKED"/>.
/// </summary>
public sealed record TabLockedFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "Database is locked by another browser tab. Close any other tab running this application and reload the page.";
}

/// <summary>
/// EF migrations could not be applied because the existing schema is
/// incompatible with the current model. <see cref="Mismatches"/> lists every
/// table/column the recovery probe found wrong. Maps to
/// <see cref="DbInitState.SCHEMA_INCOMPATIBLE"/>.
/// </summary>
public sealed record SchemaIncompatibleFailure(
    string DatabaseName,
    IReadOnlyList<SchemaMismatch> Mismatches) : IDbInitFailure
{
    public string DefaultMessage =>
        "Database schema is incompatible with the current application version. Reset the database to recreate it with the correct schema.";
}

/// <summary>
/// Worker init exceeded the timeout (typically masked by a stuck OPFS lock).
/// Maps to <see cref="DbInitState.TIMEOUT"/>.
/// </summary>
public sealed record TimeoutFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "Database initialization timed out. Close any other tab running this application and reload the page.";
}

/// <summary>
/// The browser/credential cannot supply a PRF output for the encrypted VFS
/// (PRF extension not advertised, user dismissed prompt, no enrolled
/// credential). Apps using the encrypted VFS report this when the upstream
/// PRF call fails before <c>InstallEncryptionKeyAsync</c>.
/// </summary>
public sealed record PrfUnavailableFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "No encryption key is available — passkey/PRF could not be obtained from the browser.";
}

/// <summary>
/// <c>InstallEncryptionKeyAsync</c> returned <see cref="VfsKeyInstallResult.WRONG_KEY"/>:
/// the registered key does not authenticate the existing DB's slot 0. Either
/// the wrong passkey was used or the DB file has been tampered with.
/// </summary>
public sealed record PrfCredentialMismatchFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "The supplied key does not match the existing encrypted database. Either the wrong passkey was used or the file is corrupted.";
}

/// <summary>
/// The encrypted VFS install API rejected the request for reasons other than
/// a wrong key (bad options, missing capability, worker error). Carries the
/// originating exception for diagnostics.
/// </summary>
public sealed record VfsInstallFailure(string DatabaseName, Exception Exception) : IDbInitFailure
{
    public string DefaultMessage =>
        $"Encrypted VFS install failed: {Exception.Message}";
}

/// <summary>
/// Catch-all for unexpected initialization failures. Carries the originating
/// exception so the app can render a stack trace in dev builds and a redacted
/// message in production. Maps to <see cref="DbInitState.FAILED"/>.
/// </summary>
public sealed record GenericInitFailure(string DatabaseName, Exception Exception) : IDbInitFailure
{
    public string DefaultMessage =>
        $"Database initialization failed: {Exception.GetType().Name}: {Exception.Message}";
}

/// <summary>
/// On-disk file is encrypted (slot 0 ciphertext, no SQLite magic) but the
/// worker registry has no key for it — EF migrations couldn't read schema
/// because the worker returned <c>SQLITE_NOTADB</c> on the first page read.
/// Maps to <see cref="DbInitState.ENCRYPTED_LOCKED"/>. The cure is for the
/// user to authenticate so the encryption-key install path can run; the UI
/// should stay quiet and route the user to the encryption page rather than
/// surface this as a generic error.
/// </summary>
public sealed record EncryptedDatabaseLockedFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "Database is encrypted; sign in with your passkey to unlock it.";
}
