namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Notifies subscribers that a delta envelope was applied via
/// <see cref="SyncOrchestrator.ImportAsync"/>. The orchestrator fires this
/// after the worker returns the <see cref="ImportReport"/>, while the import
/// transaction is already committed.
///
/// <para>
/// Used by downstream consumers to refresh UI state, push declarative
/// notifications to peers, or update audit logs. The default
/// <see cref="NullImportNotifier"/> is a no-op so apps that don't need the
/// signal pay nothing.
/// </para>
/// </summary>
public interface IImportNotifier
{
    /// <summary>
    /// Called once per successful <see cref="SyncOrchestrator.ImportAsync"/>.
    /// Implementations should be cheap; long-running work belongs on a
    /// queue handed off here.
    /// </summary>
    /// <param name="report">Aggregated outcome from the worker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask NotifyImportedAsync(ImportReport report, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IImportNotifier"/> for apps that don't subscribe to
/// import events. Use <see cref="Instance"/> to avoid allocations.
/// </summary>
public sealed class NullImportNotifier : IImportNotifier
{
    public static NullImportNotifier Instance { get; } = new();

    public ValueTask NotifyImportedAsync(ImportReport report, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
