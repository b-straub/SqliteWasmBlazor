using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

/// <summary>
/// Tests decimal aggregate functions using SQLite's built-in SUM, AVG, MIN, MAX.
/// EF Core translates these to standard SQL aggregates, not ef_* functions.
/// </summary>
internal class AggregateBuiltInTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_AggregateBuiltIn";

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

        // Test SUM aggregate
        var sum = await context.TypeTests.SumAsync(e => e.DecimalValue);
        if (sum != 1500.00m)
        {
            throw new InvalidOperationException($"Sum test failed: expected 1500.00, got {sum}");
        }

        // Test AVG aggregate
        var avg = await context.TypeTests.AverageAsync(e => e.DecimalValue);
        if (avg != 300.00m)
        {
            throw new InvalidOperationException($"Average test failed: expected 300.00, got {avg}");
        }

        // Test MIN aggregate
        var min = await context.TypeTests.MinAsync(e => e.DecimalValue);
        if (min != 100.00m)
        {
            throw new InvalidOperationException($"Min test failed: expected 100.00, got {min}");
        }

        // Test MAX aggregate
        var max = await context.TypeTests.MaxAsync(e => e.DecimalValue);
        if (max != 500.00m)
        {
            throw new InvalidOperationException($"Max test failed: expected 500.00, got {max}");
        }

        // Test COUNT
        var count = await context.TypeTests.CountAsync();
        if (count != 5)
        {
            throw new InvalidOperationException($"Count test failed: expected 5, got {count}");
        }

        return "OK";
    }
}
