using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

/// <summary>
/// Tests complex queries combining multiple EF Core functions.
/// This validates that all decimal arithmetic, aggregate, and comparison functions work together correctly.
/// </summary>
internal class ComplexDecimalQueryTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_ComplexDecimalQuery";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entities = new[]
        {
            new TypeTestEntity { Id = 1, DecimalValue = 100.00m, NullableDecimalValue = 50.00m },
            new TypeTestEntity { Id = 2, DecimalValue = 200.00m, NullableDecimalValue = null },
            new TypeTestEntity { Id = 3, DecimalValue = 150.00m, NullableDecimalValue = 75.00m },
            new TypeTestEntity { Id = 4, DecimalValue = 250.00m, NullableDecimalValue = 125.00m },
            new TypeTestEntity { Id = 5, DecimalValue = 300.00m, NullableDecimalValue = null }
        };

        context.TypeTests.AddRange(entities);
        await context.SaveChangesAsync();

        // Complex query: arithmetic + comparison + aggregation
        // Filter: DecimalValue * 0.1 > 15.0
        // Matches: 200 (20.0 > 15.0), 250 (25.0 > 15.0), 300 (30.0 > 15.0)
        // Does NOT match: 100 (10.0), 150 (15.0 = 15.0, not greater)
        var complexResult = await context.TypeTests
            .Where(e => (e.DecimalValue * 0.1m) > 15.0m)
            .GroupBy(e => 1)
            .Select(g => new
            {
                Count = g.Count(),
                TotalValue = g.Sum(e => e.DecimalValue),
                AverageValue = g.Average(e => e.DecimalValue),
                MaxValue = g.Max(e => e.DecimalValue),
                MinValue = g.Min(e => e.DecimalValue),
                DiscountedTotal = g.Sum(e => e.DecimalValue * 0.9m)
            })
            .FirstOrDefaultAsync();

        if (complexResult is null)
        {
            throw new InvalidOperationException("Complex query returned null");
        }

        if (complexResult.Count != 3)
        {
            throw new InvalidOperationException($"Count failed: expected 3, got {complexResult.Count}");
        }

        if (complexResult.TotalValue != 750.00m)
        {
            throw new InvalidOperationException($"TotalValue failed: expected 750.00, got {complexResult.TotalValue}");
        }

        if (complexResult.AverageValue != 250.00m)
        {
            throw new InvalidOperationException($"AverageValue failed: expected 250.00, got {complexResult.AverageValue}");
        }

        if (complexResult.MaxValue != 300.00m)
        {
            throw new InvalidOperationException($"MaxValue failed: expected 300.00, got {complexResult.MaxValue}");
        }

        if (complexResult.MinValue != 200.00m)
        {
            throw new InvalidOperationException($"MinValue failed: expected 200.00, got {complexResult.MinValue}");
        }

        if (complexResult.DiscountedTotal != 675.00m)
        {
            throw new InvalidOperationException($"DiscountedTotal failed: expected 675.00, got {complexResult.DiscountedTotal}");
        }

        // Test nullable decimal handling
        var nullableSum = await context.TypeTests
            .Where(e => e.NullableDecimalValue.HasValue)
            .SumAsync(e => e.NullableDecimalValue!.Value);

        if (nullableSum != 250.00m)
        {
            throw new InvalidOperationException($"Nullable sum failed: expected 250.00, got {nullableSum}");
        }

        return "OK";
    }
}
