using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Coverage for <see cref="EfReceiveCursorStore"/> — persists the
/// <c>HttpSyncTransport</c> receive cursor in
/// <see cref="SyncState.LastReceivedCursor"/>. Each test gets its own
/// in-memory SQLite connection so the persistence assertions are real
/// (the row outlives <c>DbContext</c> instances on the same connection).
/// </summary>
public class EfReceiveCursorStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TestSyncContext> _options;

    public EfReceiveCursorStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;
        using var seed = new TestSyncContext(_options);
        seed.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private TestSyncContext NewContext() => new(_options);

    [Fact]
    public async Task LoadAsync_NoRow_ReturnsZero()
    {
        await using var ctx = NewContext();
        var store = new EfReceiveCursorStore(ctx);

        Assert.Equal(0L, await store.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        await using var ctx = NewContext();
        var store = new EfReceiveCursorStore(ctx);

        await store.SaveAsync(42L);

        Assert.Equal(42L, await store.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_PersistsAcrossContextInstances()
    {
        await using (var writeCtx = NewContext())
        {
            await new EfReceiveCursorStore(writeCtx).SaveAsync(1234L);
        }

        // Fresh context, same DB connection — proves the row is durable.
        await using var readCtx = NewContext();
        var loaded = await new EfReceiveCursorStore(readCtx).LoadAsync();

        Assert.Equal(1234L, loaded);
    }

    [Fact]
    public async Task SaveAsync_OverwritesPreviousValue()
    {
        await using var ctx = NewContext();
        var store = new EfReceiveCursorStore(ctx);

        await store.SaveAsync(10L);
        await store.SaveAsync(99L);

        Assert.Equal(99L, await store.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_RowReusedWithSyncEngineCursor_BothColumnsPreserved()
    {
        // Critical invariant: EfReceiveCursorStore and SyncEngine.SaveCursorAsync
        // share the same single-row SyncState (keyed by EngineCursorId). Writing
        // one cursor must not clobber the other.
        await using var ctx = NewContext();
        var store = new EfReceiveCursorStore(ctx);

        // Seed an export-cursor row directly the way SyncEngine would.
        var exportTime = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        ctx.SyncStates.Add(new SyncState
        {
            Id = SyncState.EngineCursorId,
            LastExportedAt = exportTime
        });
        await ctx.SaveChangesAsync();

        // Receive-cursor save must update LastReceivedCursor without erasing LastExportedAt.
        await store.SaveAsync(7L);

        await using var verifyCtx = NewContext();
        var row = await verifyCtx.SyncStates
            .AsNoTracking()
            .FirstAsync(s => s.Id == SyncState.EngineCursorId);
        Assert.Equal(exportTime, row.LastExportedAt);
        Assert.Equal(7L, row.LastReceivedCursor);
    }

    [Fact]
    public async Task LoadAsync_ExportCursorAlreadyPresent_ReceiveCursorDefaultsToZero()
    {
        // SyncEngine seeds the row first; then a fresh EfReceiveCursorStore
        // must read 0 (not crash, not pick up LastExportedAt's ticks).
        await using var seedCtx = NewContext();
        seedCtx.SyncStates.Add(new SyncState
        {
            Id = SyncState.EngineCursorId,
            LastExportedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        await using var readCtx = NewContext();
        var loaded = await new EfReceiveCursorStore(readCtx).LoadAsync();

        Assert.Equal(0L, loaded);
    }
}
