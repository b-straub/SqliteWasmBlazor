using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;

/// <summary>
/// Shared staging for the migration-upgrade cases: bring a database to V1,
/// fill it, and hand back a context with V2 still pending.
/// </summary>
/// <remarks>
/// These do not derive from <see cref="SqliteWasmTest"/> because that base
/// creates and deletes <c>TodoDb.db</c> around every case, and these cases own
/// a different database entirely.
/// </remarks>
internal abstract class MigrationUpgradeTestBase(IServiceProvider services)
{
    /// <summary>
    /// Enough rows for the index build to be measurable without making the
    /// suite slow. The point is a real duration, not a stress test — the cost
    /// per row is what extrapolates to a consumer's database.
    /// </summary>
    protected virtual int SeedRows => 20_000;

    public abstract string Name { get; }

    protected IServiceProvider Services { get; } = services;

    protected IDbContextFactory<MigrationProbeContext> Factory =>
        Services.GetRequiredService<IDbContextFactory<MigrationProbeContext>>();

    public abstract ValueTask<string?> RunTestAsync();

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        await PrepareAsync();
        return await RunTestAsync();
    }

    /// <summary>
    /// Runs before staging. The encrypted variants install the worker-wide key
    /// here, so every database opened afterwards — the probe included — goes
    /// through the encryption VFS.
    /// </summary>
    protected virtual ValueTask PrepareAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Delete the probe database, apply V1 only, and insert
    /// <see cref="SeedRows"/> rows — the state a consumer is in when an
    /// upgrade arrives.
    /// </summary>
    protected async Task<int> StageVersion1WithDataAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        await context.Database.EnsureDeletedAsync();

        // Stop at V1: MigrateAsync() with no target applies everything, which
        // would leave nothing pending to measure.
        await context.GetService<IMigrator>().MigrateAsync(MigrationProbeContext.V1Id);

        // Batched so a large seed does not build one enormous change set.
        // Sized against what is left rather than a whole batch, so a seed
        // smaller than the batch still gets written.
        const int batchSize = 1000;
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var seeded = 0; seeded < SeedRows; seeded += batchSize)
        {
            var batch = seeded / batchSize;
            var count = Math.Min(batchSize, SeedRows - seeded);
            for (var i = 0; i < count; i++)
            {
                context.Rows.Add(new ProbeRow
                {
                    Payload = $"row-{batch}-{i}",
                    StampedAt = stamp.AddSeconds(seeded + i)
                });
            }

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        return await context.Rows.CountAsync();
    }

    /// <summary>Whether the V2 index exists in the database right now.</summary>
    protected static async Task<bool> IndexExistsAsync(MigrationProbeContext context)
    {
        var found = await context.Database
            .SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name = {0}",
                MigrationProbeContext.IndexName)
            .ToListAsync();

        return found.Count == 1;
    }

    /// <summary>
    /// Apply everything still pending and report how long it took. This is the
    /// measurement the whole exercise exists for: a consumer pays it on the
    /// boot path, with the UI already up and nothing saying why.
    /// </summary>
    protected static async Task<long> ApplyPendingAsync(MigrationProbeContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        await context.Database.MigrateAsync();
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }
}
