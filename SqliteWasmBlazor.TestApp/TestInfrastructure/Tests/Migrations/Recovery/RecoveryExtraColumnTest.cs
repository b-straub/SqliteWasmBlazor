namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Recovery;

/// <summary>
/// Drop the migrations history AND add a column the model does not declare.
/// The recovery probe must report <see cref="DbInitState.SCHEMA_INCOMPATIBLE"/>
/// with a <see cref="SchemaIncompatibleFailure"/> whose
/// <see cref="SchemaIncompatibleFailure.Mismatches"/> contains a
/// <see cref="SchemaMismatch.ExtraColumn"/> entry for the new column.
/// </summary>
internal sealed class RecoveryExtraColumnTest(IServiceProvider services)
    : MigrationRecoveryTestBase(services)
{
    public override string Name => "MigrationRecovery_ExtraColumnSurfacesMismatch";

    private const string TargetTable = "TodoItems";
    private const string ExtraColumn = "ProbeOnlyExtraColumn";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await AddColumnAsync(ctx, TargetTable, ExtraColumn);
            await DropMigrationsHistoryAsync(ctx);
        }

        await DriveBootAsync();

        if (Status.State != DbInitState.SCHEMA_INCOMPATIBLE)
        {
            return $"FAIL: expected SCHEMA_INCOMPATIBLE, got {Status.State} " +
                   $"({Status.Failure?.GetType().Name ?? "no failure"})";
        }

        if (Status.Failure is not SchemaIncompatibleFailure failure)
        {
            return $"FAIL: expected SchemaIncompatibleFailure, got " +
                   $"{Status.Failure?.GetType().Name ?? "null"}";
        }

        var hasExpectedMismatch = failure.Mismatches.Any(m =>
            string.Equals(m.Table, TargetTable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.ExtraColumn, ExtraColumn, StringComparison.OrdinalIgnoreCase));

        if (!hasExpectedMismatch)
        {
            var rendered = string.Join(
                ", ",
                failure.Mismatches.Select(m =>
                    $"{m.Table} (missing={m.MissingColumn ?? "-"}, extra={m.ExtraColumn ?? "-"})"));
            return $"FAIL: expected ExtraColumn={ExtraColumn} on {TargetTable}, " +
                   $"got [{rendered}]";
        }

        return null;
    }
}
