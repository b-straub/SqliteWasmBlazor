using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SqliteWasmBlazor;

/// <summary>
/// The one place database initialization happens. See
/// <see cref="ISqliteWasmInitializer"/> for why it is not <c>Program.cs</c>.
/// </summary>
internal sealed class SqliteWasmInitializer(
    IServiceProvider services,
    IDbInitializationReporter reporter,
    IDbInitializationStatus status,
    IOptions<SqliteWasmOptions> options,
    IEnumerable<DbContextSchemaDescriptor> contexts,
    Func<IDatabaseLockProbe?> lockProbe,
    Func<IDbInitNotifier?> notifier) : ISqliteWasmInitializer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DbContextSchemaDescriptor[] _contexts = [.. contexts];

    private bool _workerStarted;
    private bool _schemaDone;
    private (DbInitState State, IDbInitFailure? Failure) _outcome = (DbInitState.READY, null);

    /// <inheritdoc />
    public void Reset()
    {
        _schemaDone = false;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!await StartWorkerAsync(cancellationToken))
            {
                return;
            }

            await RunSchemaAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RunSchemaAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Attaches the seams the bridge cannot resolve, then starts the worker.
    /// Returns false when the caller should stop.
    /// </summary>
    private async ValueTask<bool> StartWorkerAsync(CancellationToken cancellationToken)
    {
        if (_workerStarted)
        {
            return true;
        }

        ConfigureCommandLogging(options.Value);

        // The bridge is a singleton constructed outside the container, so it
        // cannot resolve these itself. Miss the attach and a whole-pool import
        // silently stops reporting READY, leaving every AuthorizeView shut.
        SqliteWasmWorkerBridge.Instance.AttachBootStatus(reporter, status);
        SqliteWasmWorkerBridge.Instance.AttachHostDatabaseService(services.GetService<IHostDatabaseService>);

        await ReportAsync(DbInitState.INITIALIZING, null, null, cancellationToken);

        try
        {
            await SqliteWasmWorkerBridge.Instance.InitializeAsync(options.Value, cancellationToken);
        }
        catch (Exception)
        {
            // One tab per pool: OPFS synchronous access handles are exclusive.
            // Reported rather than thrown — this now runs inside a render, where
            // throwing replaces the app with Blazor's error page and says
            // nothing useful. The alert renders TabLockedFailure with a reload.
            var failure = new TabLockedFailure(FirstDatabaseName());
            await ReportAsync(DbInitState.TAB_LOCKED, failure, null, cancellationToken);
            return false;
        }

        _workerStarted = true;
        return true;
    }

    private async ValueTask RunSchemaAsync(CancellationToken cancellationToken)
    {
        if (!_workerStarted)
        {
            // EnsureSchemaAsync reached before anything started the worker.
            // Nothing to migrate against, and no diagnosis of ours to report.
            return;
        }

        if (_schemaDone)
        {
            // Done, but the caller still needs the state it produced: a second
            // unlock has to reach READY again, or the pool stays closed to
            // every AuthorizeView bound to it.
            await ReportAsync(_outcome.State, _outcome.Failure, null, cancellationToken);
            return;
        }

        // The database has to be openable, and an encrypted pool is not until a
        // key arrives — reading pending migrations would be reading ciphertext.
        // Returning without latching _schemaDone is the point: the work is
        // still owed, and UnlockAsync is what comes back for it. That is what
        // makes this safe to call from a component rendering while locked.
        //
        // Resolved per call rather than injected: the probe is implemented by
        // EncryptedSqliteWasmDatabaseService, which takes this type in its own
        // constructor, and asking for the instance up front is a cycle the
        // container refuses at startup.
        if (lockProbe() is { } probe)
        {
            var lockState = await probe.GetStateAsync(cancellationToken);
            if (lockState.Encrypted && !lockState.Unlocked)
            {
                var locked = new EncryptedDatabaseLockedFailure(FirstDatabaseName(), lockState.Hint);
                await ReportAsync(DbInitState.ENCRYPTED_LOCKED, locked, null, cancellationToken);
                return;
            }
        }

        // MIGRATING is announced by the first context that finds work, not up
        // front: most starts have nothing pending, and a state that flashes
        // every time teaches people to ignore it.
        var announced = false;
        Func<string, ValueTask> announce = async databaseName =>
        {
            if (!announced)
            {
                announced = true;
                await ReportAsync(DbInitState.MIGRATING, null, databaseName, cancellationToken);
            }
        };

        // Declaration order. The sequence stops at the first failure, because a
        // later context migrating over an earlier one's diagnosis only buries
        // it. A host with none registered — an ADO-only app — lands on READY.
        foreach (var context in _contexts)
        {
            var outcome = await context.Run(services, announce, cancellationToken);
            if (outcome.State != DbInitState.READY)
            {
                _outcome = outcome;
                _schemaDone = true;
                await ReportAsync(outcome.State, outcome.Failure, null, cancellationToken);
                return;
            }
        }

        _outcome = (DbInitState.READY, null);
        _schemaDone = true;
        await ReportAsync(DbInitState.READY, null, null, cancellationToken);
    }

    private async ValueTask ReportAsync(
        DbInitState state,
        IDbInitFailure? failure,
        string? databaseName,
        CancellationToken cancellationToken)
    {
        if (failure is null)
        {
            reporter.Report(state);
        }
        else
        {
            reporter.Report(state, failure);
        }

        if (notifier() is { } sink)
        {
            await sink.NotifyAsync(
                new DbInitNotification(state, databaseName ?? failure?.DatabaseName, failure),
                cancellationToken);
        }
    }

    /// <summary>
    /// A database name for the failures raised before any context has been
    /// opened. Empty when the host registered none.
    /// </summary>
    private string FirstDatabaseName() =>
        _contexts.Length > 0 ? _contexts[0].ResolveDatabaseName(services) : string.Empty;

    private static void ConfigureCommandLogging(SqliteWasmOptions options)
    {
        SqliteWasmLogger.CommandSqlLoggingEnabled = options.EnableCommandSqlLogging;
        SqliteWasmLogger.TracingEnabled = options.EnableRequestTracing;

        // Says so once, so a benchmarking session can tell at a glance that the
        // numbers below are actually being produced.
        SqliteWasmLogger.Trace(nameof(SqliteWasmLogger), "request tracing enabled");
    }
}
