using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// EF-backed <see cref="IReceiveCursorStore"/> that persists the receive
/// cursor in <see cref="SyncState.LastReceivedCursor"/> on the single
/// deterministic-id row keyed by <see cref="SyncState.EngineCursorId"/>.
/// Production hosts inject this so the cursor survives process restarts;
/// durability comes from the OPFS-hosted SQLite the rest of the app
/// already uses. Tests + dev keep using
/// <see cref="InMemoryReceiveCursorStore"/>.
///
/// <para>
/// Sharing the row with <see cref="SyncEngine"/>'s export cursor is
/// intentional — both are per-device transport state with the same
/// lifecycle. <see cref="SyncEngine.SaveCursorAsync"/> and
/// <see cref="SaveAsync"/> use the same fetch-or-create pattern, so
/// concurrent writers degrade to last-writer-wins on whichever column
/// raced; cursors only ever move forward, so a lost write at most replays
/// a small batch on the next pull.
/// </para>
///
/// <para>
/// <b>Lifetime.</b> Constructed with a live <see cref="CryptoSyncContextBase"/>
/// — useful for tests + ad-hoc callers. Production DI uses
/// <see cref="EfReceiveCursorStoreFactory{TContext}"/> instead, which
/// creates a fresh context per Load/Save call via
/// <see cref="IDbContextFactory{TContext}"/> and disposes it immediately —
/// the right shape for browser apps where the host is a single long-lived
/// scope.
/// </para>
/// </summary>
public sealed class EfReceiveCursorStore(CryptoSyncContextBase context) : IReceiveCursorStore
{
    public async ValueTask<long> LoadAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.SyncStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SyncState.EngineCursorId, cancellationToken)
            .ConfigureAwait(false);
        return row?.LastReceivedCursor ?? 0;
    }

    public async ValueTask SaveAsync(long cursor, CancellationToken cancellationToken = default)
    {
        var row = await context.SyncStates
            .FirstOrDefaultAsync(s => s.Id == SyncState.EngineCursorId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            context.SyncStates.Add(new SyncState
            {
                Id = SyncState.EngineCursorId,
                LastReceivedCursor = cursor
            });
        }
        else
        {
            row.LastReceivedCursor = cursor;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// DI-friendly <see cref="IReceiveCursorStore"/> wrapper that obtains a
/// fresh <typeparamref name="TContext"/> from
/// <see cref="IDbContextFactory{TContext}"/> on each Load/Save and disposes
/// it immediately. Used by <c>AddCryptoSync&lt;TContext&gt;</c> so the
/// receive cursor never holds a context across the long-lived browser
/// scope; <see cref="EfReceiveCursorStore"/> stays directly constructible
/// for tests that already manage context lifetime.
/// </summary>
public sealed class EfReceiveCursorStoreFactory<TContext>(IDbContextFactory<TContext> contextFactory)
    : IReceiveCursorStore
    where TContext : CryptoSyncContextBase
{
    public async ValueTask<long> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await new EfReceiveCursorStore(context)
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SaveAsync(long cursor, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await new EfReceiveCursorStore(context)
            .SaveAsync(cursor, cancellationToken)
            .ConfigureAwait(false);
    }
}
