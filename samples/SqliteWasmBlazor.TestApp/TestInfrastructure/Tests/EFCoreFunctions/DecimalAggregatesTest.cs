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

        return "OK";
    }
}
