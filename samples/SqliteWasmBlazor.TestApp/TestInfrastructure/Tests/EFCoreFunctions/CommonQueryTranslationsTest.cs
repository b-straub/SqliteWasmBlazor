using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class CommonQueryTranslationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_CommonQueryTranslations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var rows = new[]
        {
            new TypeTestEntity { Id = 1, StringValue = "Alpha-One", NullableStringValue = null, IntValue = 30 },
            new TypeTestEntity { Id = 2, StringValue = "Bravo-Two", NullableStringValue = "  padded  ", IntValue = 10 },
            new TypeTestEntity { Id = 3, StringValue = "Charlie-Three", NullableStringValue = "fallback", IntValue = 20 },
            new TypeTestEntity { Id = 4, StringValue = "Delta-Four", NullableStringValue = "", IntValue = 40 },
            new TypeTestEntity { Id = 5, StringValue = "Echo-Five", NullableStringValue = null, IntValue = 50 },
            new TypeTestEntity { Id = 6, StringValue = "A_100", NullableStringValue = null, IntValue = 60 }
        };

        context.TypeTests.AddRange(rows);
        await context.SaveChangesAsync();

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => EF.Functions.Like(e.StringValue, "Bravo%"))
                .Select(e => e.Id),
            [2],
            "EF.Functions.Like");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => EF.Functions.Like(e.StringValue, "A!_%", "!"))
                .Select(e => e.Id),
            [6],
            "EF.Functions.Like escape");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.StartsWith("Alpha"))
                .Select(e => e.Id),
            [1],
            "StartsWith");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.EndsWith("Two"))
                .Select(e => e.Id),
            [2],
            "EndsWith");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.Contains("lie-Th"))
                .Select(e => e.Id),
            [3],
            "Contains");

        var projection = await context.TypeTests
            .Where(e => e.Id == 2)
            .Select(e => new
            {
                Lower = e.StringValue.ToLower(),
                Upper = e.StringValue.ToUpper(),
                Replaced = e.StringValue.Replace("Two", "Second"),
                Prefix = e.StringValue.Substring(0, 5),
                Length = e.StringValue.Length,
                Trimmed = e.NullableStringValue!.Trim()
            })
            .SingleAsync();

        if (projection.Lower != "bravo-two" ||
            projection.Upper != "BRAVO-TWO" ||
            projection.Replaced != "Bravo-Second" ||
            projection.Prefix != "Bravo" ||
            projection.Length != 9 ||
            projection.Trimmed != "padded")
        {
            throw new InvalidOperationException("Common string projection translations returned unexpected values.");
        }

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => (e.NullableStringValue ?? "missing") == "missing")
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 5, 6],
            "null coalescing");

        await AssertSequenceAsync(
            context.TypeTests
                .OrderBy(e => e.IntValue)
                .Skip(1)
                .Take(3)
                .Select(e => e.Id),
            [3, 1, 4],
            "OrderBy/Skip/Take");

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
