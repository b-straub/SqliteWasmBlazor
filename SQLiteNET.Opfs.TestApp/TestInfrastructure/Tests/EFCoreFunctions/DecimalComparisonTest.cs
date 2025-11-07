using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

/// <summary>
/// Tests EF Core decimal comparison function (ef_compare) and collation (EF_DECIMAL).
/// These functions are registered in the worker and enable LINQ comparison queries with decimal values.
/// </summary>
internal class DecimalComparisonTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_DecimalComparison";

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

        // Test ef_compare - greater than
        var greaterThan = await context.TypeTests
            .Where(e => e.DecimalValue > 100)
            .OrderBy(e => e.DecimalValue)
            .Select(e => e.Id)
            .ToListAsync();
        if (!greaterThan.SequenceEqual(new[] { 1, 4, 2 }))
        {
            throw new InvalidOperationException($"Greater than test failed: expected [1,4,2], got [{string.Join(",", greaterThan)}]");
        }

        // Test ef_compare - less than
        var lessThan = await context.TypeTests
            .Where(e => e.DecimalValue < 100)
            .OrderBy(e => e.DecimalValue)
            .Select(e => e.Id)
            .ToListAsync();
        if (!lessThan.SequenceEqual(new[] { 3, 5 }))
        {
            throw new InvalidOperationException($"Less than test failed: expected [3,5], got [{string.Join(",", lessThan)}]");
        }

        // Test ef_compare - between range
        var between = await context.TypeTests
            .Where(e => e.DecimalValue >= 50 && e.DecimalValue <= 150)
            .OrderBy(e => e.DecimalValue)
            .Select(e => e.Id)
            .ToListAsync();
        if (!between.SequenceEqual(new[] { 3, 5, 1, 4 }))
        {
            throw new InvalidOperationException($"Between test failed: expected [3,5,1,4], got [{string.Join(",", between)}]");
        }

        // Test ef_compare - ordering
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
