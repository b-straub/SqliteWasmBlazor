using System.Collections.Concurrent;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Test <see cref="IImportNotifier"/> that captures every report into a
/// thread-safe list so test bodies can assert against it.
/// </summary>
internal sealed class RecordingImportNotifier : IImportNotifier
{
    private readonly ConcurrentQueue<ImportReport> _reports = new();

    public IReadOnlyCollection<ImportReport> Reports => _reports;

    public ValueTask NotifyImportedAsync(ImportReport report, CancellationToken cancellationToken = default)
    {
        _reports.Enqueue(report);
        return ValueTask.CompletedTask;
    }
}
