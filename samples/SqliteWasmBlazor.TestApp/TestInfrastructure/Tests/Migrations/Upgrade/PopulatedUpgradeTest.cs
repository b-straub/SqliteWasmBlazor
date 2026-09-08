using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;

/// <summary>
/// Apply a migration to a database that already holds rows, and prove the
/// three things nothing here proved before: the schema changed, the data
/// survived, and it took a knowable amount of time.
/// </summary>
internal class PopulatedUpgradeTest(IServiceProvider services)
    : MigrationUpgradeTestBase(services)
{
    public override string Name => "Migration_PopulatedDatabaseUpgrade";

    public override async ValueTask<string?> RunTestAsync()
    {
        var seeded = await StageVersion1WithDataAsync();
        if (seeded != SeedRows)
        {
            return $"FAIL: staged {seeded} rows, expected {SeedRows}";
        }

        await using var context = await Factory.CreateDbContextAsync();

        if (await IndexExistsAsync(context))
        {
            return "FAIL: the V2 index exists before V2 was applied — " +
                   "the staged database is not at V1";
        }

        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        if (pending is not [var onlyPending] || onlyPending != MigrationProbeContext.V2Id)
        {
            return $"FAIL: expected only {MigrationProbeContext.V2Id} pending, " +
                   $"got [{string.Join(", ", pending)}]";
        }

        var elapsedMs = await ApplyPendingAsync(context);

        if (!await IndexExistsAsync(context))
        {
            return "FAIL: migration reported success but the index does not exist";
        }

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        if (!applied.Contains(MigrationProbeContext.V2Id))
        {
            return $"FAIL: {MigrationProbeContext.V2Id} is not recorded as applied — " +
                   $"applied: [{string.Join(", ", applied)}]";
        }

        // The whole point of migrating rather than resetting.
        var after = await context.Rows.CountAsync();
        if (after != SeedRows)
        {
            return $"FAIL: {SeedRows} rows before the migration, {after} after";
        }

        // A spot check that the surviving rows are the rows, not just the count.
        var first = await context.Rows.OrderBy(r => r.Id).FirstAsync();
        if (first.Payload != "row-0-0")
        {
            return $"FAIL: first row payload is '{first.Payload}', expected 'row-0-0'";
        }

        // Not an assertion: a duration only means something next to the row
        // count that produced it, and it is what tells a consumer whether a
        // migration belongs on the boot path at their scale.
        Console.WriteLine(
            $"[{Name}] applied {MigrationProbeContext.V2Id} over {SeedRows} rows " +
            $"in {elapsedMs} ms ({(double)elapsedMs * 1000 / SeedRows:F1} us/row)");

        return null;
    }
}
