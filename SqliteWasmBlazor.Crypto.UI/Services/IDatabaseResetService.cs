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
/// <see cref="NullDatabaseResetService.Instance"/> — the panel will hide
/// the reset button and only offer the reload path.
/// </para>
/// </summary>
public interface IDatabaseResetService
{
    /// <summary>
    /// True when the implementation can actually perform a reset. The
    /// <see cref="NullDatabaseResetService"/> default returns <c>false</c>,
    /// which the alert panel uses to hide the reset button.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Perform the host-defined recovery: delete and re-migrate the
    /// affected database, then promote the boot status back to
    /// <see cref="DbInitState.READY"/>.
    /// </summary>
    ValueTask ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IDatabaseResetService"/> for hosts that don't ship
/// recovery. Use <see cref="Instance"/> to avoid allocations.
/// </summary>
public sealed class NullDatabaseResetService : IDatabaseResetService
{
    public static NullDatabaseResetService Instance { get; } = new();

    public bool IsAvailable => false;

    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
