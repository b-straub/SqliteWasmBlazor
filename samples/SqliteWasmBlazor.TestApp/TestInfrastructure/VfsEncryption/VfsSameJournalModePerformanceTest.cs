using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Crypto-cost micro-benchmark: forces both plain and encrypted paths to
/// <c>journal_mode=MEMORY</c> before inserting, so the measured delta
/// reflects ChaCha20-Poly1305 page crypto cost rather than WAL fsync I/O.
///
/// Both paths use the same <see cref="VfsTestItem"/> schema — plain via
/// <see cref="PlainVfsTestContext"/> (no key), encrypted via
/// <see cref="EncryptedTestContext"/> (test key). Combined with MEMORY
/// journal mode, the ratio isolates crypto overhead alone.
///
/// Expected reading: encrypted should be within ~1.5-2× plain for write
/// workloads. A ratio much above that indicates the crypto path is slower
/// than expected.
/// </summary>
internal sealed class VfsSameJournalModePerformanceTest
{
    private const int RowCount = 500;
    private const double CatastrophicRatio = 5.0;

    private readonly IDbContextFactory<PlainVfsTestContext> _plainFactory;
    private readonly IDbContextFactory<EncryptedTestContext> _encFactory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public VfsSameJournalModePerformanceTest(
        IDbContextFactory<PlainVfsTestContext> plainFactory,
        IDbContextFactory<EncryptedTestContext> encFactory,
        ISqliteWasmDatabaseService databaseService,
        IEncryptedSqliteWasmDatabaseService session)
    {
        _plainFactory = plainFactory;
        _encFactory = encFactory;
        _databaseService = databaseService;
        _session = session;
    }

    public string Name => "VFS_PerformanceSmoke_SameJournalMode";

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        // Set/Clear EncryptionKey are session-boundary ops: each implicitly
        // closes every currently-open DB so the next xOpen re-stamps
        // file.key from the freshly-installed globalKey state.

        // Plain phase setup — globalKey must be null.
        await _session.LockAsync();
        await using (var ctx = await _plainFactory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.EnsureCreatedAsync();
        }

        // Encrypted phase setup — globalKey = TestKey. SetEncryptionKey
        // here closes the plain DB just opened above; the next encrypted
        // open re-stamps file.key under TestKey.
        await _session.UnlockAsync(VfsEncryptionTestBase.TestKey);
        await using (var ctx = await _encFactory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.EnsureCreatedAsync();
        }

        return await RunTestAsync();
    }

    private async Task<string?> RunTestAsync()
    {
        // Phase 1 — plain. ClearEncryptionKey closes the encrypted DB just
        // created above; the plain DB then opens fresh under globalKey=null.
        await _session.LockAsync();
        var plainMs = await MeasureAsync(async () =>
        {
            await using var ctx = await _plainFactory.CreateDbContextAsync();
            // Downgrade this session's journal mode to match the encrypted path.
            // PRAGMA is session-scoped (doesn't persist on disk) — next normal
            // open reverts to WAL.
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = MEMORY;");

            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = 0; i < RowCount; i++)
            {
                ctx.Items.Add(new VfsTestItem { Marker = $"item-{i}", Payload = new string('x', 200) });
            }
            ctx.ChangeTracker.DetectChanges();
            await ctx.SaveChangesAsync();
        });

        // Phase 2 — encrypted. SetEncryptionKey closes the plain DB and
        // swaps globalKey; the next encrypted open re-stamps file.key from
        // TestKey.
        await _session.UnlockAsync(VfsEncryptionTestBase.TestKey);

        var encMs = await MeasureAsync(async () =>
        {
            await using var ctx = await _encFactory.CreateDbContextAsync();
            // Encrypted path defaults to WAL; downgrade to MEMORY to match
            // the plain-side override and isolate the crypto-only cost.
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = MEMORY;");

            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = 0; i < RowCount; i++)
            {
                ctx.Items.Add(new VfsTestItem { Marker = $"item-{i}", Payload = new string('x', 200) });
            }
            ctx.ChangeTracker.DetectChanges();
            await ctx.SaveChangesAsync();
        });

        var ratio = plainMs > 0 ? (double)encMs / plainMs : double.PositiveInfinity;

        Console.WriteLine(
            $"[{Name}] plain={plainMs} ms, encrypted={encMs} ms, ratio={ratio:F2}× ({RowCount} rows each, same schema, both journal_mode=MEMORY)");

        if (ratio > CatastrophicRatio)
        {
            return $"Encrypted path {ratio:F1}× slower than plain under equal journal modes (plain={plainMs}ms, enc={encMs}ms) — exceeds {CatastrophicRatio}× tolerance";
        }

        return null;
    }

    private static async Task<long> MeasureAsync(Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        await body();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}
