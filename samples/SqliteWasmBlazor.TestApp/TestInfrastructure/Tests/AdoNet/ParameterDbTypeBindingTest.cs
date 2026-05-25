using System.Data;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ParameterDbTypeBindingTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ParameterDbTypeBinding";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                typeof(@decimalValue),
                @decimalValue,
                typeof(@binaryValue),
                hex(@binaryValue),
                typeof(@boolValue),
                @boolValue,
                @dateValue,
                @timeValue,
                CAST(@largeIntValue AS TEXT)
            """;
        command.Parameters.Add(CreateParameter("@decimalValue", DbType.Decimal, 0.10m));
        command.Parameters.Add(CreateParameter("@binaryValue", DbType.Binary, "hi"));
        command.Parameters.Add(CreateParameter("@boolValue", DbType.Boolean, true));
        command.Parameters.Add(CreateParameter("@dateValue", DbType.Date, new DateTime(2026, 5, 24, 9, 30, 0, DateTimeKind.Utc)));
        command.Parameters.Add(CreateParameter("@timeValue", DbType.Time, new TimeOnly(14, 35, 42, 123)));
        command.Parameters.Add(CreateParameter("@largeIntValue", DbType.Int64, 9007199254740993L));

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one DbType binding row.");
        }

        if (reader.GetString(0) != "text" ||
            reader.GetString(1) != "0.10" ||
            reader.GetString(2) != "blob" ||
            reader.GetString(3) != "6869" ||
            reader.GetString(4) != "integer" ||
            reader.GetInt32(5) != 1 ||
            reader.GetString(6) != "2026-05-24" ||
            reader.GetString(7) != "14:35:42.1230000" ||
            reader.GetString(8) != "9007199254740993")
        {
            throw new InvalidOperationException(
                "Explicit DbType parameter binding did not match native-style SQLite storage classes.");
        }

        return "OK";
    }

    private static SqliteWasmParameter CreateParameter(
        string name,
        DbType dbType,
        object value)
    {
        return new SqliteWasmParameter(name, dbType)
        {
            Value = value
        };
    }
}
