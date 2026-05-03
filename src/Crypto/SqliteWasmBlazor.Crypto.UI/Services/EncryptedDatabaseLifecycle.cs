using Microsoft.AspNetCore.Components.Authorization;
using R3;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI;

namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Crypto.UI service that keeps the worker-side encryption-key registry in
/// lockstep with the C# auth state for every encrypted database the host
/// registers. Eliminates per-consumer plumbing — instead of every page
/// model writing the close-on-expiry / install-on-auth dance, the host
/// declares the database name once via
/// <c>AddCryptoUIEncryptedDatabase(name)</c> and the lifecycle service
/// handles it.
///
/// <para>
/// <b>Two transitions, one source of truth.</b>
/// <list type="bullet">
///   <item><b>Auth set</b> (sign-in / cache hydration): for every
///         registered database the service probes shape (SQLite magic
///         header on the first VERBATIM page); if encrypted, it closes
///         the DB at the worker (clears any stale registry entry +
///         satisfies <c>registerEncryptionKey</c>'s open-state guard
///         from audit-fix `257e155`) and installs the freshly-derived
///         X25519 pubkey. On <see cref="VfsKeyInstallResult.MATCH"/> AND
///         a prior boot-time <see cref="DbInitState.ENCRYPTED_LOCKED"/>,
///         it promotes the init status back to
///         <see cref="DbInitState.READY"/> so consumers see green.</item>
///   <item><b>Auth cleared</b> (TTL expiry / Lock): for every registered
///         database the service closes at the worker, dropping K from
///         the registry. Locked invariant — TTL means "lock the keys";
///         the worker registry surviving past the cache TTL would let
///         the worker decrypt pages without an active session,
///         defeating the reason TTL exists.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Wiring.</b> Subscribes to
/// <see cref="AuthenticationStateProvider.AuthenticationStateChanged"/>
/// (Blazor's standard cascade source) AND to
/// <see cref="IPrfService.KeyExpired"/> directly. The state-changed event
/// fires both transitions; the KeyExpired hook is belt-and-braces in case
/// a TTL elapsed before <see cref="PrfAuthenticationStateProvider"/>
/// noticed (no expected divergence today, but cheap insurance).
/// </para>
///
/// <para>
/// <b>Lifetime.</b> Singleton. Constructor wires both subscriptions; the
/// host is expected to eagerly resolve the service after
/// <c>builder.Build()</c> so the subscriptions are live before any page
/// renders.
/// </para>
/// </summary>
public sealed class EncryptedDatabaseLifecycle : IDisposable
{
    private static readonly byte[] SqliteMagicHeader =
    [
        0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66,
        0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00,
    ];

    private readonly HashSet<string> _databases = new(StringComparer.Ordinal);
    private readonly Lock _databasesLock = new();
    private readonly AuthenticationStateProvider _authState;
    private readonly ISqliteWasmDatabaseService _database;
    private readonly IPrfService _prf;
    private readonly IDbInitializationStatus _dbInitStatus;
    private readonly IDbInitializationReporter _dbInitReporter;
    private readonly IDisposable _keyExpiredSubscription;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public EncryptedDatabaseLifecycle(
        AuthenticationStateProvider authState,
        ISqliteWasmDatabaseService database,
        IPrfService prf,
        IDbInitializationStatus dbInitStatus,
        IDbInitializationReporter dbInitReporter,
        IEnumerable<EncryptedDatabaseRegistration> registrations)
    {
        _authState = authState;
        _database = database;
        _prf = prf;
        _dbInitStatus = dbInitStatus;
        _dbInitReporter = dbInitReporter;

        foreach (var reg in registrations)
        {
            _databases.Add(reg.DatabaseName);
        }

        _authState.AuthenticationStateChanged += OnAuthStateChanged;

        // Belt-and-braces: also subscribe to KeyExpired so a TTL elapse
        // unwires the worker registry even if the cascade hasn't fired yet.
        // Routes through the operation gate so it serializes with auth-state-
        // driven runs.
        _keyExpiredSubscription = _prf.KeyExpired
            .Subscribe(cacheKey =>
            {
                if (cacheKey.StartsWith("prf-seed:", StringComparison.Ordinal))
                {
                    _ = RunGatedAsync(() => CloseAllRegisteredAsync(default));
                }
            });
    }

    private async Task RunGatedAsync(Func<Task> action)
    {
        await _operationGate.WaitAsync();
        try { await action(); }
        catch { /* surfaces via next EF touch */ }
        finally { _operationGate.Release(); }
    }

    /// <summary>
    /// Add a database to the auto-managed set. Idempotent. Hosts call this
    /// once per encrypted database at startup (typically through the
    /// <c>AddCryptoUIEncryptedDatabase</c> service-collection extension).
    /// </summary>
    public void RegisterDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        lock (_databasesLock)
        {
            _databases.Add(databaseName);
        }
    }

    /// <summary>
    /// Probe shape (no key needed). True ⇒ slot 0 is non-magic ⇒ encrypted
    /// (or random garbage — same code path; downstream AEAD reject is the
    /// authoritative discriminator). False ⇒ plain SQLite. Null ⇒ no file.
    /// </summary>
    public async Task<bool?> IsEncryptedAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        if (!await _database.ExistsDatabaseAsync(databaseName, cancellationToken))
        {
            return null;
        }
        var bytes = await _database.ExportDatabaseAsync(
            databaseName, VfsExportMode.VERBATIM, default, cancellationToken);
        if (bytes.Length < SqliteMagicHeader.Length)
        {
            return false;
        }
        return !bytes.AsSpan(0, SqliteMagicHeader.Length).SequenceEqual(SqliteMagicHeader);
    }

    private async void OnAuthStateChanged(Task<AuthenticationState> task)
    {
        // Serialize all lifecycle operations. Two concurrent triggers happen
        // routinely: AuthenticationModel.ApplySessionAsync sets CredentialId
        // then PublicKey, each fires PushAuthState → state-provider notify →
        // this handler. Without serialization the second run's IsEncryptedAsync
        // probe closes the worker DB mid-flight (ExportDatabaseAsync internally
        // calls closeDatabase for a stable snapshot), opening the "not open"
        // window for in-flight EF Core queries that happen during the brief
        // window the AuthorizeView is Authorized but before the second run
        // re-installs the key.
        await _operationGate.WaitAsync();
        try
        {
            var state = await task;
            if (state.User.Identity?.IsAuthenticated == true)
            {
                await TryInstallAllAsync(default);
            }
            else
            {
                await CloseAllRegisteredAsync(default);
            }
        }
        catch
        {
            // Per-DB errors are swallowed to keep one failure from blocking
            // others; a real consumer-facing error happens at the next
            // EF Core touch and surfaces through the page status sink.
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task TryInstallAllAsync(CancellationToken cancellationToken)
    {
        // Re-entrancy guard. Promoting state to READY fires
        // IDbInitializationStatus.Changed → PrfAuthenticationStateProvider.
        // OnDbStateChanged → NotifyAuthenticationStateChanged → this handler
        // fires AGAIN. The re-entry would re-run IsEncryptedAsync, which
        // internally closes the worker DB (ExportDatabaseAsync VERBATIM
        // calls closeDatabase for a stable snapshot), opening a window
        // where in-flight EF Core queries hit "Database not open".
        // Skip when the DB is already known-open at the worker.
        if (_dbInitStatus.State == DbInitState.READY)
        {
            return;
        }

        var pubBase64 = _prf.GetCachedPublicKey();
        if (string.IsNullOrEmpty(pubBase64))
        {
            return;
        }
        byte[] ck;
        try
        {
            ck = Convert.FromBase64String(pubBase64);
        }
        catch (FormatException)
        {
            return;
        }
        if (ck.Length != 32)
        {
            return;
        }

        string[] snapshot;
        lock (_databasesLock)
        {
            snapshot = [.. _databases];
        }

        foreach (var db in snapshot)
        {
            try
            {
                if (await IsEncryptedAsync(db, cancellationToken) != true)
                {
                    continue;
                }

                // Close-then-install-then-open: registerEncryptionKey rejects
                // when the worker DB is open (audit-fix `257e155`); the
                // explicit open after install eagerly puts the worker DB in
                // openDatabases so the next EF Core connection.OpenAsync
                // short-circuits cleanly via the bridge-tracked state and
                // doesn't race with worker-side lazy open.
                try { await _database.CloseDatabaseAsync(db, cancellationToken); }
                catch { /* idempotent */ }
                var result = await _database.InstallEncryptionKeyAsync(db, ck, cancellationToken);
                if (result == VfsKeyInstallResult.MATCH
                    || result == VfsKeyInstallResult.NO_EXISTING_DB)
                {
                    try { await _database.OpenDatabaseAsync(db, cancellationToken); }
                    catch { /* surfaced by the next EF query if it really fails */ }
                }

                if (result == VfsKeyInstallResult.MATCH
                    && _dbInitStatus.State == DbInitState.ENCRYPTED_LOCKED)
                {
                    _dbInitReporter.Report(DbInitState.READY);
                }
            }
            catch
            {
                // Per-DB failure isolated.
            }
        }
    }

    private async Task CloseAllRegisteredAsync(CancellationToken cancellationToken)
    {
        string[] snapshot;
        lock (_databasesLock)
        {
            snapshot = [.. _databases];
        }

        // Probe each DB BEFORE closing so we know whether to downgrade global
        // state. Once closed, the registry is empty and IsEncryptedAsync
        // becomes ambiguous (still magic-header-shaped check, still correct,
        // but probing first keeps the intent obvious).
        var anyEncrypted = false;
        foreach (var db in snapshot)
        {
            bool encrypted;
            try
            {
                encrypted = await IsEncryptedAsync(db, cancellationToken) == true;
            }
            catch
            {
                encrypted = false;
            }

            try { await _database.CloseDatabaseAsync(db, cancellationToken); }
            catch { /* idempotent */ }

            if (encrypted)
            {
                anyEncrypted = true;
            }
        }

        // Downgrade global state if any closed DB was encrypted. This drives
        // the DatabaseOpen policy to fail, AuthorizeViews snap to NotAuthorized,
        // and consumer pages re-render the AuthenticationPanel. Plain-only
        // close (no encrypted DB in the registered set) leaves state alone.
        if (anyEncrypted && _dbInitStatus.State == DbInitState.READY)
        {
            // The failure carries the registered DB name(s); for a single-DB
            // app it's just the one name. Multi-DB consumers see a comma list.
            _dbInitReporter.Report(
                DbInitState.ENCRYPTED_LOCKED,
                new EncryptedDatabaseLockedFailure(string.Join(",", snapshot)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _authState.AuthenticationStateChanged -= OnAuthStateChanged;
        _keyExpiredSubscription.Dispose();
        _operationGate.Dispose();
    }
}
