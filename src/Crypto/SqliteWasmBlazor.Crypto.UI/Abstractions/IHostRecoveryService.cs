namespace SqliteWasmBlazor.Crypto.UI.Abstractions;

/// <summary>
/// The host seam plus the one thing only a UI needs: a way back from a
/// broken boot. Extends <see cref="IHostDatabaseService"/>, whose contract
/// is about databases and lives on the base plane, with the recovery
/// affordance <see cref="Components.Shared.DatabaseInformationAlert"/> and the
/// encryption panel offer.
///
/// <para>
/// Invoked on a recoverable boot failure (<see cref="SchemaIncompatibleFailure"/>,
/// <see cref="GenericInitFailure"/>, or any unmapped
/// <see cref="IDbInitFailure"/>) and by the encryption panel's Reset button.
/// The library does not own the recovery path because these panels are
/// reusable across consumer apps with different <c>DbContext</c> types and
/// database names.
/// </para>
///
/// <para>
/// Hosts write one class and register it once, with
/// <c>AddHostRecoveryService&lt;THost&gt;()</c> — that binds both this
/// interface and <see cref="IHostDatabaseService"/> to the same instance,
/// so the import paths on the base plane find the seam too. Hosts that ship
/// without recovery (read-only deployments, etc.) register
/// <see cref="NullHostRecoveryService.Instance"/>; the panel hides the reset
/// button and offers only the reload path.
/// </para>
/// </summary>
public interface IHostRecoveryService : IHostDatabaseService
{
    /// <summary>
    /// True when the implementation can actually perform a reset. The
    /// <see cref="NullHostRecoveryService"/> default returns <c>false</c>,
    /// which the alert panel uses to hide the reset button.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Perform the host-defined recovery: wipe the pool, re-migrate every
    /// owned database, then promote the boot status back to
    /// <see cref="DbInitState.READY"/>.
    /// </summary>
    ValueTask ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IHostRecoveryService"/> for hosts that don't ship
/// recovery. Use <see cref="Instance"/> to avoid allocations.
/// </summary>
public sealed class NullHostRecoveryService : IHostRecoveryService
{
    /// <summary>Shared instance — the type carries no state.</summary>
    public static NullHostRecoveryService Instance { get; } = new();

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public IReadOnlyList<string> OwnedDatabases => [];

    /// <inheritdoc />
    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask MigrateAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask ValidateSchemaAsync(
        string ownedDatabaseName,
        string probeDatabaseName,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
