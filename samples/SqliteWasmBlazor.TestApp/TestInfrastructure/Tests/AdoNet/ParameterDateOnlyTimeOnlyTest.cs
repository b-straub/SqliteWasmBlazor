using System.Data;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ParameterDateOnlyTimeOnlyTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ParameterDateOnlyTimeOnly";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = """
                CREATE TABLE ParameterDateTimeItems (
                    Id INTEGER PRIMARY KEY,
                    DateValue TEXT NOT NULL,
                    TimeValue TEXT NOT NULL
                )
                """;
            await createCommand.ExecuteNonQueryAsync();
        }

        var expectedDate = new DateOnly(2026, 5, 24);
        var expectedTime = new TimeOnly(14, 35, 42, 123);

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText = """
                INSERT INTO ParameterDateTimeItems (Id, DateValue, TimeValue)
                VALUES (1, @dateValue, @timeValue)
                """;
            insertCommand.Parameters.Add(new SqliteWasmParameter("@dateValue", expectedDate));
            insertCommand.Parameters.Add(new SqliteWasmParameter("@timeValue", expectedTime));

            var inserted = await insertCommand.ExecuteNonQueryAsync();
            if (inserted != 1)
            {
                throw new InvalidOperationException($"Expected one inserted row, got {inserted}.");
            }
        }

        await using (var explicitTypeCommand = connection.CreateCommand())
        {
            explicitTypeCommand.CommandText = """
                INSERT INTO ParameterDateTimeItems (Id, DateValue, TimeValue)
                VALUES (2, @dateValue, @timeValue)
                """;
            explicitTypeCommand.Parameters.Add(
                new SqliteWasmParameter("@dateValue", DbType.Date) { Value = expectedDate });
            explicitTypeCommand.Parameters.Add(
                new SqliteWasmParameter("@timeValue", DbType.Time) { Value = expectedTime });

            var inserted = await explicitTypeCommand.ExecuteNonQueryAsync();
            if (inserted != 1)
            {
                throw new InvalidOperationException($"Expected one explicit DbType inserted row, got {inserted}.");
            }
        }

        await using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = """
            SELECT DateValue, TimeValue, typeof(DateValue), typeof(TimeValue)
            FROM ParameterDateTimeItems
            WHERE Id IN (1, 2)
            ORDER BY Id
            """;

        await using var reader = await queryCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one DateOnly/TimeOnly parameter row.");
        }

        var actualDate = reader.GetFieldValue<DateOnly>(0);
        var actualTime = reader.GetFieldValue<TimeOnly>(1);

        if (actualDate != expectedDate)
        {
            throw new InvalidOperationException($"DateOnly parameter round-trip failed: {actualDate}.");
        }

        if (actualTime != expectedTime)
        {
            throw new InvalidOperationException($"TimeOnly parameter round-trip failed: {actualTime}.");
        }

        if (reader.GetString(2) != "text" || reader.GetString(3) != "text")
        {
            throw new InvalidOperationException(
                $"Expected DateOnly/TimeOnly parameters to bind as TEXT, got {reader.GetString(2)} / {reader.GetString(3)}.");
        }

        if (reader.GetString(0) != expectedDate.ToString("O") ||
            reader.GetString(1) != expectedTime.ToString("O"))
        {
            throw new InvalidOperationException("DateOnly/TimeOnly parameter ISO text format did not match native-style storage.");
        }

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one explicit DbType DateOnly/TimeOnly parameter row.");
        }

        if (reader.GetFieldValue<DateOnly>(0) != expectedDate ||
            reader.GetFieldValue<TimeOnly>(1) != expectedTime ||
            reader.GetString(2) != "text" ||
            reader.GetString(3) != "text")
        {
            throw new InvalidOperationException("Explicit DbType DateOnly/TimeOnly parameter round-trip failed.");
        }

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("DateOnly/TimeOnly parameter query returned more rows than expected.");
        }

        return "OK";
    }
}
