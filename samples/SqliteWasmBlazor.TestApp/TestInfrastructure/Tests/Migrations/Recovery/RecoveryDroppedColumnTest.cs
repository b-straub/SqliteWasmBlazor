namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Recovery;

/// <summary>
/// Drop the migrations history AND a real model column on TodoItems. The
/// recovery probe must report <see cref="DbInitState.SCHEMA_INCOMPATIBLE"/>
/// with a <see cref="SchemaIncompatibleFailure"/> whose
/// <see cref="SchemaIncompatibleFailure.Mismatches"/> contains a
/// <see cref="SchemaMismatch.MissingColumn"/> entry for the dropped column.
/// </summary>
internal sealed class RecoveryDroppedColumnTest(IServiceProvider services)
    : MigrationRecoveryTestBase(services)
{
    public override string Name => "MigrationRecovery_DroppedColumnSurfacesMismatch";

    private const string TargetTable = "TodoItems";
    // CompletedAt is unreferenced by the FTS5 triggers attached to
    // TodoItems (those touch Id/Title/Description/IsDeleted). Picking a
    // trigger-referenced column would fail ALTER TABLE DROP COLUMN before
    // recovery ever sees the schema.
    private const string TargetColumn = "CompletedAt";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await DropColumnAsync(ctx, TargetTable, TargetColumn);
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
            string.Equals(m.MissingColumn, TargetColumn, StringComparison.OrdinalIgnoreCase));

        if (!hasExpectedMismatch)
        {
            var rendered = string.Join(
                ", ",
                failure.Mismatches.Select(m =>
                    $"{m.Table} (missing={m.MissingColumn ?? "-"}, extra={m.ExtraColumn ?? "-"})"));
            return $"FAIL: expected MissingColumn={TargetColumn} on {TargetTable}, " +
                   $"got [{rendered}]";
        }

        return null;
    }
}
