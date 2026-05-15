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
/// PRF call fails before <c>SetEncryptionKeyAsync</c>.
/// </summary>
public sealed record PrfUnavailableFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "No encryption key is available — passkey/PRF could not be obtained from the browser.";
}

/// <summary>
/// The active global key does not authenticate the existing DB's slot 0.
/// Either the wrong passkey was used or the DB file has been tampered with.
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
/// Boot detected the OPFS lock marker — the VFS contains ciphertext databases
/// but no global key is installed in the worker. The cure is
/// <see cref="IEncryptedSqliteWasmDatabaseService.UnlockAsync"/> once the user has supplied
/// valid credentials. Maps to <see cref="DbInitState.ENCRYPTED_LOCKED"/>.
/// </summary>
/// <param name="DatabaseName">Database the boot was attempting to open.</param>
/// <param name="Hint">
/// Optional credential hint stored in the lock marker (e.g. passkey display
/// name). UI may render "Sign in with passkey: {Hint}" to disambiguate when
/// the user has multiple enrolled credentials. May be null when no hint was
/// recorded at unlock time.
/// </param>
public sealed record EncryptedDatabaseLockedFailure(
    string DatabaseName,
    string? Hint = null) : IDbInitFailure
{
    // The Hint stays on the record (AuthenticationPanel reads it to drive
    // smart-routing of the WebAuthn ceremony: hint set → targeted credential,
    // hint absent → discoverable picker). It is NOT user-facing copy; the
    // raw base64 credentialId has no meaningful display value to end users,
    // so the message stays hint-free.
    public string DefaultMessage => "Database is encrypted — sign in to unlock.";
}
