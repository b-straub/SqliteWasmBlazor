using System.Collections.Concurrent;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Test <see cref="IPushNotifier"/> that captures every notify call into a
/// thread-safe queue so test bodies can assert against payload + recipient
/// list. Mirrors <see cref="RecordingImportNotifier"/>.
/// </summary>
internal sealed class RecordingPushNotifier : IPushNotifier
{
    private readonly ConcurrentQueue<RecordedNotification> _calls = new();

    public IReadOnlyCollection<RecordedNotification> Calls => _calls;

    public ValueTask NotifyAsync(
        byte[] payload,
        IReadOnlyList<string> recipientPublicKeys,
        CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new RecordedNotification(payload, [.. recipientPublicKeys]));
        return ValueTask.CompletedTask;
    }

    public sealed record RecordedNotification(byte[] Payload, string[] RecipientPublicKeys);
}
