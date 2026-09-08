using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;

/// <summary>
/// What the next boot does after a migration died between doing its work and
/// recording that it had.
/// </summary>
/// <remarks>
/// <para>
/// A migration is not atomic with its history row on every provider and every
/// interruption — a tab closed mid-upgrade can leave the schema changed and
/// <c>__EFMigrationsHistory</c> not yet written. That state is reproduced here
/// directly: run V2's effect by hand, leave the history alone, then boot.
/// </para>
/// <para>
/// This test asserts what actually happens rather than what should: EF has no
/// idea the work was done, replays <c>CREATE INDEX</c>, and SQLite refuses. The
/// value is that the failure is loud and the data is untouched — a consumer
/// hitting this gets an error naming the object, not a silently half-migrated
/// database. If EF or the provider ever makes this recoverable, this test is
/// the thing that will notice.
/// </para>
/// </remarks>
internal sealed class InterruptedUpgradeTest(IServiceProvider services)
    : MigrationUpgradeTestBase(services)
{
    public override string Name => "Migration_InterruptedUpgradeFailsLoudly";

    public override async ValueTask<string?> RunTestAsync()
    {
        await StageVersion1WithDataAsync();

        await using var context = await Factory.CreateDbContextAsync();

        // The interruption: V2's work is done, its history row is not.
        await context.Database.ExecuteSqlRawAsync(
            $"CREATE INDEX \"{MigrationProbeContext.IndexName}\" ON \"ProbeRows\" (\"StampedAt\")");

        if (!await IndexExistsAsync(context))
        {
            return "FAIL: could not stage the half-applied state";
        }

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        if (applied.Contains(MigrationProbeContext.V2Id))
        {
            return "FAIL: V2 is recorded as applied — the staged state is not half-applied";
        }

        string? failure = null;
        try
        {
            await context.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure is null)
        {
            return "FAIL: replaying an already-applied migration succeeded silently. " +
                   "That is a behaviour change worth looking at — it means EF or the " +
                   "provider now tolerates the half-applied state this test stages.";
        }

        // Loud, and about the right object.
        if (!failure.Contains(MigrationProbeContext.IndexName, StringComparison.OrdinalIgnoreCase))
        {
            return $"FAIL: migration failed but the message does not name " +
                   $"{MigrationProbeContext.IndexName}: {failure}";
        }

        // The important half: a failed migration must not cost data.
        var rows = await context.Rows.CountAsync();
        if (rows != SeedRows)
        {
            return $"FAIL: {SeedRows} rows before the failed migration, {rows} after";
        }

        Console.WriteLine($"[{Name}] failed as expected: {failure}");
        return null;
    }
}
