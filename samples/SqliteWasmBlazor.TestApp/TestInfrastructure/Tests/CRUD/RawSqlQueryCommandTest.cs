using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

internal class RawSqlQueryCommandTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "RawSql_QueryCommand";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        context.TypeTests.AddRange(
            CreateRow(1, "raw-alpha", 10),
            CreateRow(2, "raw-beta", 20),
            CreateRow(3, "other", 30));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var rawRows = await context.TypeTests
            .FromSqlInterpolated($"SELECT * FROM TypeTests WHERE StringValue LIKE {"raw-%"}")
            .Where(e => e.IntValue >= 15)
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.StringValue })
            .ToListAsync();

        if (rawRows.Count != 1 ||
            rawRows[0] is not { Id: 2, StringValue: "raw-beta" })
        {
            throw new InvalidOperationException("FromSqlInterpolated composition returned unexpected rows.");
        }

        var affected = await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE TypeTests SET NullableStringValue = {"updated-by-raw-sql"} WHERE StringValue LIKE {"raw-%"}");
        if (affected != 2)
        {
            throw new InvalidOperationException($"ExecuteSqlInterpolatedAsync affected {affected} rows; expected 2.");
        }

        var updatedCount = await context.TypeTests
            .CountAsync(e => e.NullableStringValue == "updated-by-raw-sql");
        if (updatedCount != 2)
        {
            throw new InvalidOperationException($"Expected 2 raw-SQL-updated rows, got {updatedCount}.");
        }

        var scalarCount = await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM TypeTests WHERE NullableStringValue = {"updated-by-raw-sql"}")
            .SingleAsync();
        if (scalarCount != 2)
        {
            throw new InvalidOperationException($"SqlQuery scalar count returned {scalarCount}; expected 2.");
        }

        return "OK";
    }

    private static TypeTestEntity CreateRow(int id, string stringValue, int intValue)
    {
        return new TypeTestEntity
        {
            Id = id,
            StringValue = stringValue,
            IntValue = intValue,
            BoolValue = true,
            DateTimeValue = DateTime.UtcNow,
            DateTimeOffsetValue = DateTimeOffset.UtcNow,
            GuidValue = Guid.NewGuid(),
            EnumValue = TestEnum.FIRST,
            CharValue = 'R'
        };
    }
}
