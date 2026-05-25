using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ReaderDateOnlyTimeOnlyTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ReaderDateOnlyTimeOnly";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                '2026-05-24' AS DateText,
                '14:35:42.123' AS TimeText,
                2461184.5 AS JulianDate,
                2461184.8958333335 AS JulianDateTime
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one DateOnly/TimeOnly row.");
        }

        var date = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("DateText"));
        var time = reader.GetFieldValue<TimeOnly>(reader.GetOrdinal("TimeText"));
        var julianDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("JulianDate"));
        var julianDateTime = reader.GetFieldValue<DateTime>(reader.GetOrdinal("JulianDateTime"));
        var julianDateTimeOffset = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("JulianDateTime"));

        if (date != new DateOnly(2026, 5, 24))
        {
            throw new InvalidOperationException($"DateOnly TEXT conversion failed: {date}.");
        }

        if (time != new TimeOnly(14, 35, 42, 123))
        {
            throw new InvalidOperationException($"TimeOnly TEXT conversion failed: {time}.");
        }

        if (julianDate != new DateOnly(2026, 5, 24))
        {
            throw new InvalidOperationException($"DateOnly Julian day conversion failed: {julianDate}.");
        }

        var expectedJulianDateTime = new DateTime(2026, 5, 24, 9, 30, 0, DateTimeKind.Unspecified);
        if (Math.Abs((julianDateTime - expectedJulianDateTime).TotalMilliseconds) > 1)
        {
            throw new InvalidOperationException($"DateTime Julian day conversion failed: {julianDateTime:O}.");
        }

        var expectedJulianDateTimeOffset = new DateTimeOffset(expectedJulianDateTime, TimeSpan.Zero);
        if (Math.Abs((julianDateTimeOffset - expectedJulianDateTimeOffset).TotalMilliseconds) > 1 ||
            julianDateTimeOffset.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"DateTimeOffset Julian day conversion failed: {julianDateTimeOffset:O}.");
        }

        return "OK";
    }
}
