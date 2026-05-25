using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

/// <summary>
/// Tests EF Core decimal aggregate functions (ef_sum, ef_avg, ef_min, ef_max).
/// These functions are registered in the worker and enable LINQ aggregation queries with decimal values.
/// </summary>
internal class DecimalAggregatesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_DecimalAggregates";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entities = new[]
        {
            new TypeTestEntity { Id = 1, DecimalValue = 100.00m },
            new TypeTestEntity { Id = 2, DecimalValue = 200.00m },
            new TypeTestEntity { Id = 3, DecimalValue = 300.00m },
            new TypeTestEntity { Id = 4, DecimalValue = 400.00m },
            new TypeTestEntity { Id = 5, DecimalValue = 500.00m }
        };

        context.TypeTests.AddRange(entities);
        await context.SaveChangesAsync();

        // Test ef_sum - sum aggregate
        var sum = await context.TypeTests.SumAsync(e => e.DecimalValue);
        if (sum != 1500.00m)
        {
            throw new InvalidOperationException($"Sum test failed: expected 1500.00, got {sum}");
        }

        // Test ef_avg - average aggregate
        var avg = await context.TypeTests.AverageAsync(e => e.DecimalValue);
        if (avg != 300.00m)
        {
            throw new InvalidOperationException($"Average test failed: expected 300.00, got {avg}");
        }

        // Test ef_min - minimum aggregate
        var min = await context.TypeTests.MinAsync(e => e.DecimalValue);
        if (min != 100.00m)
        {
            throw new InvalidOperationException($"Min test failed: expected 100.00, got {min}");
        }

        // Test ef_max - maximum aggregate
        var max = await context.TypeTests.MaxAsync(e => e.DecimalValue);
        if (max != 500.00m)
        {
            throw new InvalidOperationException($"Max test failed: expected 500.00, got {max}");
        }

        context.TypeTests.AddRange(
            new TypeTestEntity { Id = 6, DecimalValue = 0.10m },
            new TypeTestEntity { Id = 7, DecimalValue = 0.20m },
            new TypeTestEntity { Id = 8, DecimalValue = 9007199254740993.01m },
            new TypeTestEntity { Id = 9, DecimalValue = 9007199254740993.02m });
        await context.SaveChangesAsync();

        var preciseSum = await context.TypeTests
            .Where(e => e.Id == 6 || e.Id == 7)
            .SumAsync(e => e.DecimalValue);
        if (preciseSum != 0.30m)
        {
            throw new InvalidOperationException($"Precise sum failed: expected 0.30, got {preciseSum}");
        }

        var preciseAverage = await context.TypeTests
            .Where(e => e.Id == 6 || e.Id == 7)
            .AverageAsync(e => e.DecimalValue);
        if (preciseAverage != 0.15m)
        {
            throw new InvalidOperationException($"Precise average failed: expected 0.15, got {preciseAverage}");
        }

        var largeSum = await context.TypeTests
            .Where(e => e.Id == 8 || e.Id == 9)
            .SumAsync(e => e.DecimalValue);
        if (largeSum != 18014398509481986.03m)
        {
            throw new InvalidOperationException($"Large sum failed: expected 18014398509481986.03, got {largeSum}");
        }

        return "OK";
    }
}
