namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Per-actor view of a shared <see cref="InMemorySyncRelay"/>. Implements
/// <see cref="ISyncTransport"/> so xUnit scenarios can drive the orchestrator
/// and invitation flows against the same broadcast contract a real HTTP relay
/// impl carries. One transport per actor; multiple transports share the same
/// relay so envelopes flow between them.
/// </summary>
internal sealed class InMemorySyncTransport(InMemorySyncRelay relay) : ISyncTransport
{
    public ValueTask SendAsync(
        byte[] envelope,
        CancellationToken cancellationToken = default)
    {
        relay.Enqueue(envelope);
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> TryReceiveAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(relay.TryDequeue());
}
