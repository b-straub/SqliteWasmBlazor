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

    public VfsSameJournalModePerformanceTest(
        IDbContextFactory<PlainVfsTestContext> plainFactory,
        IDbContextFactory<EncryptedTestContext> encFactory,
        ISqliteWasmDatabaseService databaseService)
    {
        _plainFactory = plainFactory;
        _encFactory = encFactory;
        _databaseService = databaseService;
    }

    public string Name => "VFS_PerformanceSmoke_SameJournalMode";

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        await using (var ctx = await _plainFactory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.EnsureCreatedAsync();
        }
        await using (var ctx = await _encFactory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.EnsureCreatedAsync();
        }

        return await RunTestAsync();
    }

    private async Task<string?> RunTestAsync()
    {
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
