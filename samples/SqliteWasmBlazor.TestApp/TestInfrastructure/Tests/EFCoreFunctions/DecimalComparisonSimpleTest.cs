using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

/// <summary>
/// Tests simple decimal comparisons that EF Core translates to SQL.
/// EF Core does NOT use ef_* functions for these - they use standard SQL operators.
/// This test verifies that decimal comparisons work correctly in SQLite WASM.
/// </summary>
internal class DecimalComparisonSimpleTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_DecimalComparisonSimple";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entities = new[]
        {
            new TypeTestEntity { Id = 1, DecimalValue = 100.50m },
            new TypeTestEntity { Id = 2, DecimalValue = 200.75m },
            new TypeTestEntity { Id = 3, DecimalValue = 50.25m },
            new TypeTestEntity { Id = 4, DecimalValue = 150.00m },
            new TypeTestEntity { Id = 5, DecimalValue = 75.00m }
        };

        context.TypeTests.AddRange(entities);
        await context.SaveChangesAsync();

        // Test simple greater than comparison
        var greaterThan = await context.TypeTests
            .Where(e => e.DecimalValue > 100)
            .OrderBy(e => e.DecimalValue)
            .Select(e => e.Id)
            .ToListAsync();
        if (!greaterThan.SequenceEqual(new[] { 1, 4, 2 }))
        {
            throw new InvalidOperationException($"Greater than test failed: expected [1,4,2], got [{string.Join(",", greaterThan)}]");
        }

        // Test simple less than comparison
        var lessThan = await context.TypeTests
            .Where(e => e.DecimalValue < 100)
            .OrderBy(e => e.DecimalValue)
            .Select(e => e.Id)
            .ToListAsync();
        if (!lessThan.SequenceEqual(new[] { 3, 5 }))
        {
            throw new InvalidOperationException($"Less than test failed: expected [3,5], got [{string.Join(",", lessThan)}]");
        }

        // Test between range
        var between = await context.TypeTests
            .Where(e => e.DecimalValue >= 50 && e.DecimalValue <= 150)
            .OrderBy(e => e.DecimalValue)
            .Select(e => e.Id)
            .ToListAsync();
        if (!between.SequenceEqual(new[] { 3, 5, 1, 4 }))
        {
            throw new InvalidOperationException($"Between test failed: expected [3,5,1,4], got [{string.Join(",", between)}]");
        }

        // Test ordering (descending)
        var ordered = await context.TypeTests
            .OrderByDescending(e => e.DecimalValue)
            .Select(e => e.Id)
            .ToListAsync();
        if (!ordered.SequenceEqual(new[] { 2, 4, 1, 5, 3 }))
        {
            throw new InvalidOperationException($"Ordering test failed: expected [2,4,1,5,3], got [{string.Join(",", ordered)}]");
        }

        return "OK";
    }
}
