namespace SqliteWasmBlazor;

/// <summary>
/// Holds the per-context schema steps registered by
/// <c>InitializeSqliteWasmDatabaseAsync</c> and runs them once the database is
/// openable. See <see cref="IDbSchemaInitializer"/> for why they are not run
/// where they are registered.
/// </summary>
internal sealed class DbSchemaInitializer(
    IDbInitializationReporter reporter,
    IDbInitializationStatus status,
    Func<IDatabaseLockProbe?> lockProbe) : IDbSchemaInitializer
{
    /// <summary>
    /// One step per context. Each reports what it ended in; anything other
    /// than <see cref="DbInitState.READY"/> stops the sequence, because a
    /// second context migrating over a diagnosis for the first only buries it.
    /// </summary>
    internal delegate Task<(DbInitState State, IDbInitFailure? Failure)> SchemaStep(
        Func<ValueTask> onWorkStarting,
        CancellationToken cancellationToken);

    private readonly List<SchemaStep> _steps = [];
    private readonly HashSet<Type> _registered = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _hasRun;
    private (DbInitState State, IDbInitFailure? Failure) _outcome = (DbInitState.READY, null);

    /// <inheritdoc />
    public void Reset() => _hasRun = false;

    /// <summary>
    /// Adds the step for <paramref name="contextType"/>, once. A host may
    /// re-drive initialization — the recovery tests do, and so does anything
    /// retrying a failed boot — and a step registered twice would migrate
    /// twice.
    /// </summary>
    internal void Register(Type contextType, SchemaStep step)
    {
        lock (_steps)
        {
            if (_registered.Add(contextType))
            {
                _steps.Add(step);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        // Triggers are deliberately plural — a component on first render, an
        // unlock, an import — so more than one can arrive, and an encrypted
        // pool can be locked and unlocked repeatedly within one session.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_hasRun)
            {
                // The work is done, but the caller still needs the state it
                // produced: a second unlock has to reach READY again, or the
                // pool stays locked to every AuthorizeView bound to it.
                Report(_outcome);
                return;
            }

            // A boot stage already failed: that diagnosis is the useful one,
            // and it is not this step's to overwrite.
            if (status.State is DbInitState.TAB_LOCKED
                              or DbInitState.TIMEOUT
                              or DbInitState.FAILED
                              or DbInitState.SCHEMA_INCOMPATIBLE)
            {
                _hasRun = true;
                _outcome = (status.State, status.Failure);
                return;
            }

            // The database has to be openable, and an encrypted pool is not
            // until a key arrives — GetPendingMigrationsAsync would be reading
            // ciphertext. Returning without setting _hasRun is the point: the
            // work is still owed, and UnlockAsync is what comes back for it.
            // This is what makes the call safe from any trigger, including a
            // component rendering while the pool is still locked.
            // Resolved per call, not injected: the probe is implemented by
            // EncryptedSqliteWasmDatabaseService, which takes this type in its
            // own constructor. Asking for the instance up front is a cycle the
            // container refuses, and it takes the whole app down at startup.
            if (lockProbe() is { } probe)
            {
                var lockState = await probe.GetStateAsync();
                if (lockState.Encrypted && !lockState.Unlocked)
                {
                    return;
                }
            }

            SchemaStep[] steps;
            lock (_steps)
            {
                steps = [.. _steps];
            }

            // MIGRATING is reported by the first step that finds work, not up
            // front: most starts have nothing pending, and a state that
            // flashes every time teaches people to ignore it.
            var announced = false;
            var announce = () =>
            {
                if (!announced)
                {
                    announced = true;
                    reporter.Report(DbInitState.MIGRATING);
                }

                return ValueTask.CompletedTask;
            };

            // An ADO-only host registers none, and lands on READY below.
            foreach (var step in steps)
            {
                var outcome = await step(announce, cancellationToken);
                if (outcome.State != DbInitState.READY)
                {
                    _outcome = outcome;
                    _hasRun = true;
                    Report(_outcome);
                    return;
                }
            }

            _outcome = (DbInitState.READY, null);
            _hasRun = true;
            Report(_outcome);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Report((DbInitState State, IDbInitFailure? Failure) outcome)
    {
        if (outcome.Failure is null)
        {
            reporter.Report(outcome.State);
        }
        else
        {
            reporter.Report(outcome.State, outcome.Failure);
        }
    }
}
