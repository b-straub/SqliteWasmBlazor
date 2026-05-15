namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Recovery;

/// <summary>
/// Healthy schema with a missing <c>__EFMigrationsHistory</c> table: the
/// recovery probe should rebuild the history, find every model column
/// present, and the boot helper should land on <see cref="DbInitState.READY"/>
/// with no <see cref="IDbInitFailure"/>.
///
/// This is the "false-positive" guard for shadow properties / discriminator
/// columns: if the recovery's column-by-column check produced spurious
/// mismatches on a healthy schema, this test would fail with
/// <see cref="DbInitState.SCHEMA_INCOMPATIBLE"/>.
/// </summary>
internal sealed class RecoveryHistoryRebuildTest(IServiceProvider services)
    : MigrationRecoveryTestBase(services)
{
    public override string Name => "MigrationRecovery_HistoryRebuildSucceeds";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await DropMigrationsHistoryAsync(ctx);
        }

        await DriveBootAsync();

        if (Status.State != DbInitState.READY)
        {
            return $"FAIL: expected READY after recovery, got {Status.State} " +
                   $"({Status.Failure?.GetType().Name ?? "no failure"}: " +
                   $"{Status.Failure?.DefaultMessage ?? "n/a"})";
        }

        if (Status.Failure is not null)
        {
            return $"FAIL: expected null Failure on READY, got {Status.Failure.GetType().Name}";
        }

        return null;
    }
}
