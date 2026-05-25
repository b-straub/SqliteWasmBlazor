using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class CollectionPredicateTranslationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_CollectionPredicateTranslations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        context.TypeTests.AddRange(
            CreateRow(1, 10, "Alpha", TestEnum.FIRST, TestEnum.FIRST),
            CreateRow(2, 20, "Bravo", TestEnum.SECOND, null),
            CreateRow(3, 30, "Charlie", TestEnum.THIRD, TestEnum.SECOND),
            CreateRow(4, 40, "Delta", TestEnum.NONE, TestEnum.THIRD));
        await context.SaveChangesAsync();

        var selectedIds = new[] { 1, 3, 99 };
        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => selectedIds.Contains(e.Id))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 3],
            "integer collection Contains");

        var selectedNames = new[] { "Alpha", "Delta" };
        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => selectedNames.Contains(e.StringValue))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 4],
            "string collection Contains");

        var selectedEnums = new[] { TestEnum.SECOND, TestEnum.THIRD };
        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => selectedEnums.Contains(e.EnumValue))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [2, 3],
            "enum collection Contains");

        var nullableEnums = new TestEnum?[] { null, TestEnum.THIRD };
        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => nullableEnums.Contains(e.NullableEnumValue))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [2, 4],
            "nullable enum collection Contains");

        var emptyIds = Array.Empty<int>();
        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => emptyIds.Contains(e.Id))
                .Select(e => e.Id),
            [],
            "empty collection Contains");

        return "OK";
    }

    private static TypeTestEntity CreateRow(
        int id,
        int intValue,
        string stringValue,
        TestEnum enumValue,
        TestEnum? nullableEnumValue)
    {
        return new TypeTestEntity
        {
            Id = id,
            IntValue = intValue,
            StringValue = stringValue,
            EnumValue = enumValue,
            NullableEnumValue = nullableEnumValue,
            DateTimeValue = new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
            DateTimeOffsetValue = new DateTimeOffset(2026, 5, 24, 9, 0, 0, TimeSpan.Zero),
            GuidValue = Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"),
            CharValue = 'C'
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
