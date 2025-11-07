using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

/// <summary>
/// Tests EF Core decimal arithmetic functions (ef_add, ef_divide, ef_multiply, ef_negate, ef_mod).
/// These functions are registered in the worker and enable LINQ queries with decimal operations.
/// </summary>
internal class DecimalArithmeticTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_DecimalArithmetic";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entities = new[]
        {
            new TypeTestEntity { Id = 1, DecimalValue = 100.50m },
            new TypeTestEntity { Id = 2, DecimalValue = 50.25m },
            new TypeTestEntity { Id = 3, DecimalValue = 75.00m },
            new TypeTestEntity { Id = 4, DecimalValue = 200.00m },
            new TypeTestEntity { Id = 5, DecimalValue = 33.33m }
        };

        context.TypeTests.AddRange(entities);
        await context.SaveChangesAsync();

        // Test ef_add - addition
        var addResult = await context.TypeTests
            .Where(e => e.DecimalValue + 50 > 100)
            .CountAsync();
        if (addResult != 4)
        {
            throw new InvalidOperationException($"Addition test failed: expected 4, got {addResult}");
        }

        // Test ef_multiply - multiplication
        var multiplyResult = await context.TypeTests
            .Where(e => e.DecimalValue * 2 > 150)
            .CountAsync();
        if (multiplyResult != 2)
        {
            throw new InvalidOperationException($"Multiplication test failed: expected 2, got {multiplyResult}");
        }

        // Test ef_divide - division
        var divideResult = await context.TypeTests
            .Where(e => e.DecimalValue / 2 > 40)
            .CountAsync();
        if (divideResult != 2)
        {
            throw new InvalidOperationException($"Division test failed: expected 2, got {divideResult}");
        }

        // Test ef_negate - negation
        var negateResult = await context.TypeTests
            .Select(e => new { e.Id, Negated = -e.DecimalValue })
            .FirstAsync(e => e.Id == 1);
        if (negateResult.Negated != -100.50m)
        {
            throw new InvalidOperationException($"Negation test failed: expected -100.50, got {negateResult.Negated}");
        }

        // Test ef_mod - modulo
        var modResult = await context.TypeTests
            .Where(e => e.DecimalValue % 25 == 0.25m)
            .CountAsync();
        if (modResult != 1)
        {
            throw new InvalidOperationException($"Modulo test failed: expected 1, got {modResult}");
        }

        return "OK";
    }
}
