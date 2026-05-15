namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Host-supplied seam invoked by <see cref="Components.Shared.DatabaseErrorAlert"/>
/// when the user requests recovery on a recoverable boot failure
/// (<see cref="SchemaIncompatibleFailure"/>, <see cref="GenericInitFailure"/>,
/// or any unmapped <see cref="IDbInitFailure"/>). The host typically deletes
/// the affected database, re-runs migrations, and promotes
/// <see cref="IDbInitializationStatus"/> back to <see cref="DbInitState.READY"/>.
///
/// <para>
/// The library intentionally does not own the recovery path because the
/// CryptoSync.UI panels are reusable across consumer apps with different
/// <c>DbContext</c> types and database names. Hosts that ship without
/// recovery (read-only deployments, etc.) register
/// <see cref="NullHostDatabaseService.Instance"/> — the panel will hide
/// the reset button and only offer the reload path.
/// </para>
/// </summary>
public interface IHostDatabaseService
{
    /// <summary>
    /// True when the implementation can actually perform a reset. The
    /// <see cref="NullHostDatabaseService"/> default returns <c>false</c>,
    /// which the alert panel uses to hide the reset button.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Perform the host-defined recovery: delete and re-migrate the
    /// affected database, then promote the boot status back to
    /// <see cref="DbInitState.READY"/>.
    /// </summary>
    ValueTask ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when at least one of the host's registered DbContexts holds
    /// at least one row. Drives the visibility of the Plain-disk
    /// ZIP-export affordance on the encryption page — empty databases
    /// produce a meaningless download. The reset service is the natural
    /// home for the predicate because it already knows the host's
    /// DbContext factories (it has to, to drive per-context
    /// <c>MigrateAsync</c>).
    /// <para>
    /// Implementations should fail-open (return <c>true</c>) on probe
    /// errors so transient query failures don't suppress an
    /// otherwise-valid export affordance.
    /// </para>
    /// </summary>
    ValueTask<bool> HasAnyDataAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IHostDatabaseService"/> for hosts that don't ship
/// recovery. Use <see cref="Instance"/> to avoid allocations.
/// </summary>
public sealed class NullHostDatabaseService : IHostDatabaseService
{
    public static NullHostDatabaseService Instance { get; } = new();

    public bool IsAvailable => false;

    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<bool> HasAnyDataAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);
}
