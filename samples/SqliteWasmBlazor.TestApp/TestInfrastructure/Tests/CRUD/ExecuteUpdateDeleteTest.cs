using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

internal class ExecuteUpdateDeleteTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExecuteUpdateDelete_BulkOperations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        context.TypeTests.AddRange(
            CreateRow(1, "Keep", 10, false),
            CreateRow(2, "Bulk", 20, false),
            CreateRow(3, "Bulk", 30, false),
            CreateRow(4, "Delete", 40, false));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var updated = await context.TypeTests
            .Where(e => e.StringValue == "Bulk")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.BoolValue, true)
                .SetProperty(e => e.IntValue, e => e.IntValue + 5)
                .SetProperty(e => e.NullableStringValue, "updated"));

        if (updated != 2)
        {
            throw new InvalidOperationException($"ExecuteUpdateAsync affected {updated} rows; expected 2.");
        }

        var updatedRows = await context.TypeTests
            .Where(e => e.StringValue == "Bulk")
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.IntValue, e.BoolValue, e.NullableStringValue })
            .ToListAsync();

        if (updatedRows.Count != 2 ||
            updatedRows[0] is not { Id: 2, IntValue: 25, BoolValue: true, NullableStringValue: "updated" } ||
            updatedRows[1] is not { Id: 3, IntValue: 35, BoolValue: true, NullableStringValue: "updated" })
        {
            throw new InvalidOperationException("ExecuteUpdateAsync did not persist expected values.");
        }

        var deleted = await context.TypeTests
            .Where(e => e.StringValue == "Delete")
            .ExecuteDeleteAsync();

        if (deleted != 1)
        {
            throw new InvalidOperationException($"ExecuteDeleteAsync affected {deleted} rows; expected 1.");
        }

        var remainingIds = await context.TypeTests
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();

        if (!remainingIds.SequenceEqual(new[] { 1, 2, 3 }))
        {
            throw new InvalidOperationException(
                $"ExecuteDeleteAsync left unexpected rows [{string.Join(",", remainingIds)}].");
        }

        return "OK";
    }

    private static TypeTestEntity CreateRow(int id, string group, int intValue, bool boolValue)
    {
        return new TypeTestEntity
        {
            Id = id,
            StringValue = group,
            IntValue = intValue,
            BoolValue = boolValue,
            DateTimeValue = DateTime.UtcNow,
            DateTimeOffsetValue = DateTimeOffset.UtcNow,
            GuidValue = Guid.NewGuid(),
            EnumValue = TestEnum.FIRST,
            CharValue = 'B'
        };
    }
}
