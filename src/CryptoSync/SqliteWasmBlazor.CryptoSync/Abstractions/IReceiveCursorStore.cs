namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Persistence seam for <see cref="HttpSyncTransport"/>'s receive cursor —
/// the relay-side <c>cursor</c> integer the GET request passes via
/// <c>?since=…</c>. Without persistence a process restart drains the
/// inbox from cursor 0, replaying every envelope ever delivered (P9
/// violation, threat-model §7).
///
/// <para>
/// The interface is byte-opaque to where the cursor lives. Built-in impls:
/// <see cref="InMemoryReceiveCursorStore"/> for tests + dev, and
/// <see cref="EfReceiveCursorStore"/> for production browser apps —
/// durability comes from the OPFS-hosted SQLite the rest of the app
/// already uses, via <see cref="SyncState.LastReceivedCursor"/>. Consumer
/// chooses the impl in DI; <see cref="HttpSyncTransport"/> doesn't care.
/// </para>
/// </summary>
public interface IReceiveCursorStore
{
    /// <summary>
    /// Read the most recent cursor. Returns 0 if no cursor has been saved
    /// yet — that's the "drain everything" signal the relay accepts.
    /// </summary>
    ValueTask<long> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a new cursor. Implementations should be best-effort:
    /// failure to save means the next process may replay envelopes, not
    /// data loss.
    /// </summary>
    ValueTask SaveAsync(long cursor, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IReceiveCursorStore"/> — the default for tests +
/// development. Production should swap in an OPFS-backed impl so the
/// cursor survives reload.
/// </summary>
public sealed class InMemoryReceiveCursorStore : IReceiveCursorStore
{
    private long _cursor;

    public ValueTask<long> LoadAsync(CancellationToken cancellationToken = default)
        => new(_cursor);

    public ValueTask SaveAsync(long cursor, CancellationToken cancellationToken = default)
    {
        _cursor = cursor;
        return ValueTask.CompletedTask;
    }
}
