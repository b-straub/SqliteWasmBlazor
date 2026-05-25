using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class TypedPredicateTranslationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_TypedPredicateTranslations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var baseDate = new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);
        var guidA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var guidB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var guidC = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var guidD = Guid.Parse("44444444-4444-4444-4444-444444444444");

        context.TypeTests.AddRange(
            CreateRow(1, true, true, baseDate, baseDate.AddDays(1), guidA, TestEnum.FIRST, TestEnum.FIRST),
            CreateRow(2, false, false, baseDate.AddDays(1), null, guidB, TestEnum.SECOND, null),
            CreateRow(3, true, null, baseDate.AddDays(2), baseDate.AddDays(3), guidC, TestEnum.THIRD, TestEnum.SECOND),
            CreateRow(4, false, true, baseDate.AddDays(3), null, guidD, TestEnum.NONE, TestEnum.THIRD));
        await context.SaveChangesAsync();

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.BoolValue)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 3],
            "bool predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.NullableBoolValue == true)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 4],
            "nullable bool predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.NullableBoolValue == null)
                .Select(e => e.Id),
            [3],
            "nullable bool null predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.DateTimeValue >= baseDate.AddDays(1) && e.DateTimeValue < baseDate.AddDays(3))
                .OrderBy(e => e.DateTimeValue)
                .Select(e => e.Id),
            [2, 3],
            "DateTime range predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.NullableDateTimeValue.HasValue)
                .OrderBy(e => e.NullableDateTimeValue)
                .Select(e => e.Id),
            [1, 3],
            "nullable DateTime predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.EnumValue >= TestEnum.SECOND)
                .OrderBy(e => e.EnumValue)
                .Select(e => e.Id),
            [2, 3],
            "enum comparison predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.EnumValue.HasFlag(TestEnum.FIRST))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 3],
            "enum HasFlag predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.NullableEnumValue == null)
                .Select(e => e.Id),
            [2],
            "nullable enum predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => (e.NullableEnumValue ?? TestEnum.SECOND) == TestEnum.SECOND)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [2, 3],
            "nullable enum coalesce predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.NullableBoolValue == true)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 4],
            "nullable bool true predicate");

        var selectedGuids = new[] { guidA, guidC };
        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => selectedGuids.Contains(e.GuidValue))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 3],
            "Guid IN predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .OrderByDescending(e => e.DateTimeValue)
                .Select(e => e.Id),
            [4, 3, 2, 1],
            "DateTime ordering");

        var anyActiveFirst = await context.TypeTests
            .AnyAsync(e => e.BoolValue && e.EnumValue == TestEnum.FIRST);
        if (!anyActiveFirst)
        {
            throw new InvalidOperationException("Any predicate returned false.");
        }

        var allAfterBaseDate = await context.TypeTests
            .AllAsync(e => e.DateTimeValue >= baseDate);
        if (!allAfterBaseDate)
        {
            throw new InvalidOperationException("All predicate returned false.");
        }

        return "OK";
    }

    private static TypeTestEntity CreateRow(
        int id,
        bool boolValue,
        bool? nullableBoolValue,
        DateTime dateTimeValue,
        DateTime? nullableDateTimeValue,
        Guid guidValue,
        TestEnum enumValue,
        TestEnum? nullableEnumValue)
    {
        return new TypeTestEntity
        {
            Id = id,
            BoolValue = boolValue,
            NullableBoolValue = nullableBoolValue,
            DateTimeValue = dateTimeValue,
            NullableDateTimeValue = nullableDateTimeValue,
            DateTimeOffsetValue = new DateTimeOffset(dateTimeValue),
            GuidValue = guidValue,
            NullableGuidValue = guidValue,
            EnumValue = enumValue,
            NullableEnumValue = nullableEnumValue,
            StringValue = $"Typed row {id}"
        };
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
