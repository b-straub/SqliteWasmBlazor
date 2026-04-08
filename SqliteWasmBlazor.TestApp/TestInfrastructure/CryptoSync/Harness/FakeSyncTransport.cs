namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync.Harness;

/// <summary>
/// In-memory envelope queue between actors. Simulates the network layer for
/// integration scenarios without touching the real relay. Forged-workflow
/// scenarios install a <see cref="Tamper"/> hook to mutate bytes mid-flight
/// (flip a ciphertext bit, strip the signature, replace the sender key, etc.).
/// </summary>
internal sealed class FakeSyncTransport
{
    private readonly Dictionary<string, Queue<byte[]>> _queues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional byte-level mutation applied to every envelope as it flows
    /// through the transport. When set, scenarios can simulate tampered
    /// envelopes, signature stripping, or recipient-list replacement.
    /// </summary>
    public Func<byte[], byte[]>? Tamper { get; set; }

    /// <summary>
    /// Broadcast an envelope to each of the specified recipients. If
    /// <see cref="Tamper"/> is installed, the mutation applies before delivery.
    /// The delivered byte array is cloned per recipient so tampering on one
    /// does not contaminate another's copy.
    /// </summary>
    public void Send(byte[] envelope, IEnumerable<string> toActorNames)
    {
        var delivered = Tamper is not null ? Tamper(envelope) : envelope;
        foreach (var to in toActorNames)
        {
            if (!_queues.TryGetValue(to, out var queue))
            {
                queue = new Queue<byte[]>();
                _queues[to] = queue;
            }
            // Clone so forgery tests can tamper with one recipient's copy
            // without affecting another's.
            queue.Enqueue((byte[])delivered.Clone());
        }
    }

    /// <summary>
    /// Dequeue the next pending envelope for <paramref name="actorName"/>, or
    /// <c>null</c> if the actor's inbox is empty.
    /// </summary>
    public byte[]? Receive(string actorName)
    {
        if (_queues.TryGetValue(actorName, out var queue) && queue.Count > 0)
        {
            return queue.Dequeue();
        }
        return null;
    }

    /// <summary>Number of pending envelopes for an actor.</summary>
    public int PendingCount(string actorName)
    {
        return _queues.TryGetValue(actorName, out var queue) ? queue.Count : 0;
    }

    /// <summary>Clear all pending envelopes (used between scenario phases).</summary>
    public void Clear()
    {
        _queues.Clear();
        Tamper = null;
    }
}
