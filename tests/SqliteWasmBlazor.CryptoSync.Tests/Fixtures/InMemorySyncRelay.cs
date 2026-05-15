using System.Collections.Concurrent;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Process-local backplane for <see cref="InMemorySyncTransport"/>. Mirrors
/// the whitelist-broadcast wire model: one global FIFO queue. Senders enqueue
/// once, every receiver drains from the same queue (each clones their copy
/// so a per-receiver mutation can't contaminate another's bytes).
///
/// <para>
/// The optional <see cref="Tamper"/> hook mutates the envelope as it flows
/// through — used by forgery / negative tests to flip a ciphertext bit,
/// strip a signature, etc. Mutation runs once per send, before any clone, so
/// receivers see consistent tampered bytes.
/// </para>
/// </summary>
internal sealed class InMemorySyncRelay
{
    private readonly ConcurrentQueue<byte[]> _queue = new();

    public Func<byte[], byte[]>? Tamper { get; set; }

    public void Enqueue(byte[] envelope)
    {
        var delivered = Tamper is null ? envelope : Tamper(envelope);
        _queue.Enqueue((byte[])delivered.Clone());
    }

    public byte[]? TryDequeue()
        => _queue.TryDequeue(out var bytes) ? bytes : null;

    public int PendingCount => _queue.Count;
}
