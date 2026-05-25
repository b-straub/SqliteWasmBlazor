using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativeDateTimeFunctionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativeDateTimeFunctions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                date('2026-05-24 09:30:15') AS DateValue,
                time('2026-05-24 09:30:15') AS TimeValue,
                time('2026-05-24 09:30:15.123', 'subsec') AS SubsecondTimeValue,
                datetime('2026-05-24 09:30:15', '+1 day', '+2 hours') AS DateTimeModifierValue,
                datetime('2026-05-24 09:30:15', 'start of month', '+1 month', '-1 day') AS MonthEndValue,
                date('2026-05-24', 'weekday 1') AS WeekdayValue,
                julianday('2000-01-01 12:00:00') AS JulianDayValue,
                unixepoch('1970-01-02 00:00:00') AS UnixEpochValue,
                unixepoch('1970-01-02 00:00:00.250', 'subsec') AS UnixEpochSubsecondValue,
                datetime(86400, 'unixepoch') AS UnixEpochDateTimeValue,
                strftime('%Y-%m-%dT%H:%M:%fZ', '2026-05-24 09:30:15.123') AS StrftimeValue,
                datetime(
                    '2026-05-24 09:30:15',
                    timediff('2026-05-25 10:30:15', '2026-05-24 09:30:15')
                ) AS TimeDiffAppliedValue
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native date/time function row.");
        }

        AssertEqual("2026-05-24", reader.GetString(reader.GetOrdinal("DateValue")), "date");
        AssertEqual("09:30:15", reader.GetString(reader.GetOrdinal("TimeValue")), "time");
        AssertEqual("09:30:15.123", reader.GetString(reader.GetOrdinal("SubsecondTimeValue")), "time subsec");
        AssertEqual("2026-05-25 11:30:15", reader.GetString(reader.GetOrdinal("DateTimeModifierValue")), "datetime modifiers");
        AssertEqual("2026-05-31 00:00:00", reader.GetString(reader.GetOrdinal("MonthEndValue")), "start of month modifier");
        AssertEqual("2026-05-25", reader.GetString(reader.GetOrdinal("WeekdayValue")), "weekday modifier");
        AssertClose(2451545.0, reader.GetDouble(reader.GetOrdinal("JulianDayValue")), "julianday");
        AssertEqual(86400, reader.GetInt32(reader.GetOrdinal("UnixEpochValue")), "unixepoch");
        AssertClose(86400.25, reader.GetDouble(reader.GetOrdinal("UnixEpochSubsecondValue")), "unixepoch subsec");
        AssertEqual("1970-01-02 00:00:00", reader.GetString(reader.GetOrdinal("UnixEpochDateTimeValue")), "unixepoch modifier");
        AssertEqual("2026-05-24T09:30:15.123Z", reader.GetString(reader.GetOrdinal("StrftimeValue")), "strftime");
        AssertEqual("2026-05-25 10:30:15", reader.GetString(reader.GetOrdinal("TimeDiffAppliedValue")), "timediff");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native date/time function row.");
        }

        return "OK";
    }

    private static void AssertEqual<T>(T expected, T actual, string functionName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned {actual}; expected {expected}.");
        }
    }

    private static void AssertClose(double expected, double actual, string functionName)
    {
        if (Math.Abs(expected - actual) > 0.0000001)
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned {actual}; expected {expected}.");
        }
    }
}
