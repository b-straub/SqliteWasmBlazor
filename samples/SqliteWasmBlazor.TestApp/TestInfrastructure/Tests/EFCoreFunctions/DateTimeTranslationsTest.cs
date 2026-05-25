using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class DateTimeTranslationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_DateTimeTranslations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var mayMorning = new DateTime(2026, 5, 24, 9, 30, 15, 123, DateTimeKind.Utc);
        var mayLateMorning = new DateTime(2026, 5, 25, 10, 45, 20, DateTimeKind.Utc);
        var januaryNight = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var mayEvening = new DateTime(2026, 5, 24, 18, 5, 40, DateTimeKind.Utc);

        context.TypeTests.AddRange(
            CreateRow(1, mayMorning, mayMorning.AddDays(1), "May morning"),
            CreateRow(2, mayLateMorning, null, "May late morning"),
            CreateRow(3, januaryNight, januaryNight.AddDays(24), "January night"),
            CreateRow(4, mayEvening, mayEvening.AddMonths(1), "May evening"));
        await context.SaveChangesAsync();

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.DateTimeValue.Year == 2026)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 2, 4],
            "DateTime.Year predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.DateTimeValue.Month == 5 && e.DateTimeValue.Day == 24)
                .OrderBy(e => e.DateTimeValue)
                .Select(e => e.Id),
            [1, 4],
            "DateTime.Month/Day predicate and ordering");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.DateTimeValue.Hour >= 9 && e.DateTimeValue.Minute >= 30)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 2],
            "DateTime.Hour/Minute predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.DateTimeValue.Second == 5)
                .Select(e => e.Id),
            [3],
            "DateTime.Second predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.DateTimeValue.DayOfYear == 144)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 4],
            "DateTime.DayOfYear predicate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.NullableDateTimeValue.HasValue &&
                            e.NullableDateTimeValue.Value.Month == 6)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [4],
            "nullable DateTime member predicate");

        var groupedCounts = await context.TypeTests
            .GroupBy(e => e.DateTimeValue.Year)
            .OrderBy(e => e.Key)
            .Select(e => new { Year = e.Key, Count = e.Count() })
            .ToListAsync();

        if (groupedCounts.Count != 2 ||
            groupedCounts[0].Year != 2026 ||
            groupedCounts[0].Count != 3 ||
            groupedCounts[1].Year != 2027 ||
            groupedCounts[1].Count != 1)
        {
            throw new InvalidOperationException("DateTime.Year grouping returned unexpected results.");
        }

        var dateProjection = await context.TypeTests
            .Where(e => e.Id == 1)
            .Select(e => new
            {
                Date = e.DateTimeValue.Date,
                TimeOfDay = e.DateTimeValue.TimeOfDay,
                Millisecond = e.DateTimeValue.Millisecond,
                DayOfWeek = e.DateTimeValue.DayOfWeek,
                AddDays = e.DateTimeValue.AddDays(1),
                AddHours = e.DateTimeValue.AddHours(2),
                AddMinutes = e.DateTimeValue.AddMinutes(3),
                AddSeconds = e.DateTimeValue.AddSeconds(4),
                AddMilliseconds = e.DateTimeValue.AddMilliseconds(500),
                AddTicks = e.DateTimeValue.AddTicks(10_000_000),
                AddMonths = e.DateTimeValue.AddMonths(1),
                AddYears = e.DateTimeValue.AddYears(1),
                DateOnly = DateOnly.FromDateTime(e.DateTimeValue)
            })
            .SingleAsync();

        if (dateProjection.Date != mayMorning.Date ||
            dateProjection.TimeOfDay.Hours != 9 ||
            dateProjection.TimeOfDay.Minutes != 30 ||
            dateProjection.TimeOfDay.Seconds != 15 ||
            dateProjection.Millisecond != 123 ||
            dateProjection.DayOfWeek != DayOfWeek.Sunday ||
            dateProjection.AddDays.Day != 25 ||
            dateProjection.AddHours.Hour != 11 ||
            dateProjection.AddMinutes.Minute != 33 ||
            dateProjection.AddSeconds.Second != 19 ||
            dateProjection.AddMilliseconds.Millisecond != 623 ||
            dateProjection.AddTicks.Second != 16 ||
            dateProjection.AddMonths.Month != 6 ||
            dateProjection.AddYears.Year != 2027 ||
            dateProjection.DateOnly != new DateOnly(2026, 5, 24))
        {
            throw new InvalidOperationException("DateTime additive/member translations returned unexpected values.");
        }

        var clockProjection = await context.TypeTests
            .Select(e => new
            {
                Now = DateTime.Now,
                Today = DateTime.Today,
                UtcNow = DateTime.UtcNow
            })
            .FirstAsync();

        if (clockProjection.Now == default ||
            clockProjection.Today.TimeOfDay != TimeSpan.Zero ||
            clockProjection.UtcNow == default)
        {
            throw new InvalidOperationException("DateTime current-clock translations returned unexpected values.");
        }

        return "OK";
    }

    private static TypeTestEntity CreateRow(
        int id,
        DateTime dateTimeValue,
        DateTime? nullableDateTimeValue,
        string label)
    {
        return new TypeTestEntity
        {
            Id = id,
            DateTimeValue = dateTimeValue,
            NullableDateTimeValue = nullableDateTimeValue,
            DateTimeOffsetValue = new DateTimeOffset(dateTimeValue),
            NullableDateTimeOffsetValue = nullableDateTimeValue is null
                ? null
                : new DateTimeOffset(nullableDateTimeValue.Value),
            TimeSpanValue = TimeSpan.FromMinutes(id),
            NullableTimeSpanValue = TimeSpan.FromHours(id),
            GuidValue = Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"),
            NullableGuidValue = Guid.Parse($"10000000-0000-0000-0000-{id:000000000000}"),
            EnumValue = TestEnum.FIRST,
            NullableEnumValue = TestEnum.SECOND,
            CharValue = (char)('A' + id - 1),
            NullableCharValue = (char)('a' + id - 1),
            StringValue = label
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
