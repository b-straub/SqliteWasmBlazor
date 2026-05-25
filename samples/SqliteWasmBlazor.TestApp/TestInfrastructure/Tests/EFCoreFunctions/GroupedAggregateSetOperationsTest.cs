using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class GroupedAggregateSetOperationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_GroupedAggregateSetOperations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        context.TypeTests.AddRange(
            new TypeTestEntity { Id = 1, StringValue = "Alpha", NullableStringValue = "north", BoolValue = true, IntValue = 10, DecimalValue = 1.25m },
            new TypeTestEntity { Id = 2, StringValue = "Bravo", NullableStringValue = "north", BoolValue = true, IntValue = 20, DecimalValue = 2.25m },
            new TypeTestEntity { Id = 3, StringValue = "Charlie", NullableStringValue = "south", BoolValue = false, IntValue = 30, DecimalValue = 3.25m },
            new TypeTestEntity { Id = 4, StringValue = "Delta", NullableStringValue = "south", BoolValue = false, IntValue = 40, DecimalValue = 4.25m },
            new TypeTestEntity { Id = 5, StringValue = "Echo", NullableStringValue = null, BoolValue = true, IntValue = 50, DecimalValue = 5.25m });
        await context.SaveChangesAsync();

        var grouped = await context.TypeTests
            .GroupBy(e => e.BoolValue)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                LongCount = g.LongCount(),
                Sum = g.Sum(e => e.IntValue),
                Min = g.Min(e => e.IntValue),
                Max = g.Max(e => e.IntValue),
                Average = g.Average(e => e.IntValue)
            })
            .OrderBy(g => g.Key)
            .ToListAsync();

        if (grouped.Count != 2)
        {
            throw new InvalidOperationException($"Expected two grouped aggregate rows, got {grouped.Count}.");
        }

        var falseGroup = grouped[0];
        var trueGroup = grouped[1];
        if (falseGroup.Key ||
            falseGroup.Count != 2 ||
            falseGroup.LongCount != 2L ||
            falseGroup.Sum != 70 ||
            falseGroup.Min != 30 ||
            falseGroup.Max != 40 ||
            Math.Abs(falseGroup.Average - 35.0) > 0.0001)
        {
            throw new InvalidOperationException("Grouped aggregate false bucket returned unexpected values.");
        }

        if (!trueGroup.Key ||
            trueGroup.Count != 3 ||
            trueGroup.LongCount != 3L ||
            trueGroup.Sum != 80 ||
            trueGroup.Min != 10 ||
            trueGroup.Max != 50 ||
            Math.Abs(trueGroup.Average - 80.0 / 3.0) > 0.0001)
        {
            throw new InvalidOperationException("Grouped aggregate true bucket returned unexpected values.");
        }

        var stringAggregates = await context.TypeTests
            .Where(e => e.NullableStringValue != null)
            .GroupBy(e => e.NullableStringValue)
            .Select(g => new
            {
                Region = g.Key,
                Concatenated = string.Concat(g.OrderBy(e => e.Id).Select(e => e.StringValue)),
                Joined = string.Join("|", g.OrderBy(e => e.Id).Select(e => e.StringValue))
            })
            .OrderBy(g => g.Region)
            .ToListAsync();

        if (stringAggregates.Count != 2 ||
            stringAggregates[0].Region != "north" ||
            stringAggregates[0].Concatenated != "AlphaBravo" ||
            stringAggregates[0].Joined != "Alpha|Bravo" ||
            stringAggregates[1].Region != "south" ||
            stringAggregates[1].Concatenated != "CharlieDelta" ||
            stringAggregates[1].Joined != "Charlie|Delta")
        {
            throw new InvalidOperationException("Grouped string aggregate translations returned unexpected values.");
        }

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.IntValue <= 30)
                .Select(e => e.Id)
                .Union(context.TypeTests.Where(e => e.IntValue >= 40).Select(e => e.Id))
                .OrderBy(id => id),
            [1, 2, 3, 4, 5],
            "Union");

        await AssertSequenceAsync(
            context.TypeTests
                .Select(e => e.Id)
                .Except(context.TypeTests.Where(e => e.BoolValue).Select(e => e.Id))
                .OrderBy(id => id),
            [3, 4],
            "Except");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.IntValue >= 20)
                .Select(e => e.Id)
                .Intersect(context.TypeTests.Where(e => e.IntValue <= 40).Select(e => e.Id))
                .OrderBy(id => id),
            [2, 3, 4],
            "Intersect");

        var distinctRegions = await context.TypeTests
            .Where(e => e.NullableStringValue != null)
            .Select(e => e.NullableStringValue)
            .Distinct()
            .OrderBy(region => region)
            .ToListAsync();
        if (!distinctRegions.SequenceEqual(["north", "south"]))
        {
            throw new InvalidOperationException(
                $"Distinct failed: expected [north,south], got [{string.Join(",", distinctRegions)}].");
        }

        return "OK";
    }

    private static async Task AssertSequenceAsync(
        IQueryable<int> query,
        int[] expected,
        string operation)
    {
        var actual = await query.ToListAsync();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
        }
    }
}
