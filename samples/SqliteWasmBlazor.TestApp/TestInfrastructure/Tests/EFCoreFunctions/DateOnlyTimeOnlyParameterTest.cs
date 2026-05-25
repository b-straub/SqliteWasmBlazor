using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class DateOnlyTimeOnlyParameterTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_DateOnlyTimeOnlyParameters";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var expectedDate = new DateOnly(2026, 5, 24);
        var expectedTime = new TimeOnly(14, 35, 42, 123);

        var dateResult = await context.Database
            .SqlQuery<DateOnly>($"SELECT {expectedDate} AS Value")
            .SingleAsync();
        if (dateResult != expectedDate)
        {
            throw new InvalidOperationException($"DateOnly EF SQL parameter round-trip failed: {dateResult}.");
        }

        var timeResult = await context.Database
            .SqlQuery<TimeOnly>($"SELECT {expectedTime} AS Value")
            .SingleAsync();
        if (timeResult != expectedTime)
        {
            throw new InvalidOperationException($"TimeOnly EF SQL parameter round-trip failed: {timeResult}.");
        }

        var storageClass = await context.Database
            .SqlQuery<string>($"SELECT typeof({expectedDate}) || '/' || typeof({expectedTime}) AS Value")
            .SingleAsync();
        if (storageClass != "text/text")
        {
            throw new InvalidOperationException(
                $"Expected DateOnly/TimeOnly EF parameters to bind as TEXT, got {storageClass}.");
        }

        var dateProjection = await context.Database
            .SqlQuery<DateOnly>($"SELECT {expectedDate} AS Value")
            .Select(value => new
            {
                AddDays = value.AddDays(1),
                AddMonths = value.AddMonths(1),
                AddYears = value.AddYears(1),
                Day = value.Day,
                DayOfWeek = value.DayOfWeek,
                DayOfYear = value.DayOfYear,
                DayNumber = value.DayNumber,
                Month = value.Month,
                Year = value.Year
            })
            .SingleAsync();

        if (dateProjection.AddDays != new DateOnly(2026, 5, 25) ||
            dateProjection.AddMonths != new DateOnly(2026, 6, 24) ||
            dateProjection.AddYears != new DateOnly(2027, 5, 24) ||
            dateProjection.Day != 24 ||
            dateProjection.DayOfWeek != DayOfWeek.Sunday ||
            dateProjection.DayOfYear != 144 ||
            dateProjection.DayNumber != expectedDate.DayNumber ||
            dateProjection.Month != 5 ||
            dateProjection.Year != 2026)
        {
            throw new InvalidOperationException("DateOnly member translations returned unexpected values.");
        }

        var timeProjection = await context.Database
            .SqlQuery<TimeOnly>($"SELECT {expectedTime} AS Value")
            .Select(value => new
            {
                AddHours = value.AddHours(1),
                AddMinutes = value.AddMinutes(2),
                Hour = value.Hour,
                IsBetween = value.IsBetween(new TimeOnly(14, 0), new TimeOnly(15, 0)),
                Millisecond = value.Millisecond,
                Minute = value.Minute,
                Second = value.Second
            })
            .SingleAsync();

        if (timeProjection.AddHours != new TimeOnly(15, 35, 42, 123) ||
            timeProjection.AddMinutes != new TimeOnly(14, 37, 42, 123) ||
            timeProjection.Hour != 14 ||
            !timeProjection.IsBetween ||
            timeProjection.Millisecond != 123 ||
            timeProjection.Minute != 35 ||
            timeProjection.Second != 42)
        {
            throw new InvalidOperationException("TimeOnly member translations returned unexpected values.");
        }

        return "OK";
    }
}
