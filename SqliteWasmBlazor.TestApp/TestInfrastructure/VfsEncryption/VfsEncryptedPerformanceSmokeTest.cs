using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Default-configuration perf smoke: plain DB vs encrypted DB on the
/// <b>same</b> VfsTestItem schema, both in the default
/// <c>journal_mode=WAL</c>. The plain side opens through the VFS without
/// a key; the encrypted side opens with the deterministic test key.
///
/// Because both sides now insert an identical row shape into an identical
/// single-table schema, the ratio is dominated by page-level AEAD cost
/// rather than schema-complexity differences. Fails only on a
/// catastrophic slowdown (encrypted &gt; 10× plain).
/// </summary>
internal sealed class VfsEncryptedPerformanceSmokeTest
{
    // 500 rows × ~200 bytes ≈ 100 KB, comfortably multi-page for both plain
    // and encrypted. Higher counts produce exponentially more EF Core
    // ChangeTracking log events in Debug builds.
    private const int RowCount = 500;
    private const double CatastrophicRatio = 10.0;

    private readonly IDbContextFactory<PlainVfsTestContext> _plainFactory;
    private readonly IDbContextFactory<EncryptedTestContext> _encFactory;
    private readonly ISqliteWasmDatabaseService _databaseService;

    public VfsEncryptedPerformanceSmokeTest(
        IDbContextFactory<PlainVfsTestContext> plainFactory,
        IDbContextFactory<EncryptedTestContext> encFactory,
        ISqliteWasmDatabaseService databaseService)
    {
        _plainFactory = plainFactory;
        _encFactory = encFactory;
        _databaseService = databaseService;
    }

    public string Name => "VFS_PerformanceSmoke";

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
            $"[{Name}] plain={plainMs} ms (WAL), encrypted={encMs} ms (WAL), " +
            $"ratio={ratio:F2}× ({RowCount} rows each, same schema) — default modes are " +
            $"WAL on both sides");

        if (ratio > CatastrophicRatio)
        {
            return $"Encrypted path {ratio:F1}× slower than plain (plain={plainMs}ms, enc={encMs}ms) — exceeds {CatastrophicRatio}× tolerance";
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
