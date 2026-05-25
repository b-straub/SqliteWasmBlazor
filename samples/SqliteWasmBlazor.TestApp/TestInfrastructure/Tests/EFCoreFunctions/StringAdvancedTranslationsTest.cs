using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class StringAdvancedTranslationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_StringAdvancedTranslations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        context.TypeTests.AddRange(
            new TypeTestEntity { Id = 1, StringValue = "  Alpha  ", NullableStringValue = null },
            new TypeTestEntity { Id = 2, StringValue = "  Bravo-Two  ", NullableStringValue = "  padded  " },
            new TypeTestEntity { Id = 3, StringValue = "Charlie-Three", NullableStringValue = "fallback" },
            new TypeTestEntity { Id = 4, StringValue = "", NullableStringValue = "" },
            new TypeTestEntity { Id = 5, StringValue = "Echo-Five", NullableStringValue = null },
            new TypeTestEntity { Id = 6, StringValue = "  z  ", NullableStringValue = "   " });
        await context.SaveChangesAsync();

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => string.IsNullOrEmpty(e.StringValue))
                .Select(e => e.Id),
            [4],
            "string.IsNullOrEmpty non-nullable");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => string.IsNullOrEmpty(e.NullableStringValue))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 4, 5],
            "string.IsNullOrEmpty nullable");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => string.IsNullOrWhiteSpace(e.NullableStringValue))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 4, 5, 6],
            "string.IsNullOrWhiteSpace nullable");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.TrimStart().StartsWith("Bravo"))
                .Select(e => e.Id),
            [2],
            "TrimStart + StartsWith");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.TrimEnd().EndsWith("Two"))
                .Select(e => e.Id),
            [2],
            "TrimEnd + EndsWith");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.IndexOf("Two") >= 0)
                .Select(e => e.Id),
            [2],
            "IndexOf predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.Contains('-'))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [2, 3, 5],
            "Contains char predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.StringValue.StartsWith('C') && e.StringValue.EndsWith('e'))
                .Select(e => e.Id),
            [3],
            "StartsWith/EndsWith char predicate");

        var projection = await context.TypeTests
            .Where(e => e.Id == 2)
            .Select(e => new
            {
                LeftTrimmed = e.StringValue.TrimStart(),
                RightTrimmed = e.StringValue.TrimEnd(),
                SegmentIndex = e.StringValue.IndexOf("Two"),
                SegmentIndexFromOffset = e.StringValue.IndexOf("Two", 6),
                NullableTrimmed = e.NullableStringValue!.TrimEnd()
            })
            .SingleAsync();

        if (projection.LeftTrimmed != "Bravo-Two  " ||
            projection.RightTrimmed != "  Bravo-Two" ||
            projection.SegmentIndex != 8 ||
            projection.SegmentIndexFromOffset != 8 ||
            projection.NullableTrimmed != "  padded")
        {
            throw new InvalidOperationException(
                "Advanced string projection translations returned unexpected values: " +
                $"LeftTrimmed={projection.LeftTrimmed}, " +
                $"RightTrimmed={projection.RightTrimmed}, " +
                $"SegmentIndex={projection.SegmentIndex}, " +
                $"SegmentIndexFromOffset={projection.SegmentIndexFromOffset}, " +
                $"NullableTrimmed={projection.NullableTrimmed}.");
        }

        var charProjection = await context.TypeTests
            .Where(e => e.Id == 2)
            .Select(e => new
            {
                TrimmedWithChar = e.StringValue.Trim(' '),
                LeftTrimmedWithChar = e.StringValue.TrimStart(' '),
                RightTrimmedWithChar = e.StringValue.TrimEnd(' '),
                FirstChar = e.StringValue.TrimStart().FirstOrDefault(),
                LastChar = e.StringValue.TrimEnd().LastOrDefault(),
                CompareTo = e.StringValue.Trim().CompareTo("Bravo-Two"),
                StaticCompare = string.Compare(e.StringValue.Trim(), "Bravo-Two"),
                Concatenated = string.Concat(e.StringValue.Trim(), "!")
            })
            .SingleAsync();

        if (charProjection.TrimmedWithChar != "Bravo-Two" ||
            charProjection.LeftTrimmedWithChar != "Bravo-Two  " ||
            charProjection.RightTrimmedWithChar != "  Bravo-Two" ||
            charProjection.FirstChar != 'B' ||
            charProjection.LastChar != 'o' ||
            charProjection.CompareTo != 0 ||
            charProjection.StaticCompare != 0 ||
            charProjection.Concatenated != "Bravo-Two!")
        {
            throw new InvalidOperationException("SQLite string function translations returned unexpected values.");
        }

        var charOverloadProjection = await context.TypeTests
            .Where(e => e.Id == 3)
            .Select(e => new
            {
                ContainsDash = e.StringValue.Contains('-'),
                StartsWithChar = e.StringValue.StartsWith('C'),
                EndsWithChar = e.StringValue.EndsWith('e'),
                DashIndex = e.StringValue.IndexOf('-'),
                ReplacedDash = e.StringValue.Replace('-', '/'),
                Suffix = e.StringValue.Substring(8)
            })
            .SingleAsync();

        if (!charOverloadProjection.ContainsDash ||
            !charOverloadProjection.StartsWithChar ||
            !charOverloadProjection.EndsWithChar ||
            charOverloadProjection.DashIndex != 7 ||
            charOverloadProjection.ReplacedDash != "Charlie/Three" ||
            charOverloadProjection.Suffix != "Three")
        {
            throw new InvalidOperationException("SQLite string char-overload translations returned unexpected values.");
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
