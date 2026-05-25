using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ReaderTypedFieldValuesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ReaderTypedFieldValues";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                1 AS BoolValue,
                255 AS ByteValue,
                -8 AS SByteValue,
                32000 AS ShortValue,
                65000 AS UShortValue,
                123456 AS IntValue,
                4000000000 AS UIntValue,
                9007199254740991 AS LongValue,
                42 AS ULongValue,
                12.5 AS FloatValue,
                99.125 AS DoubleValue,
                '123456.789' AS DecimalValue,
                '2026-05-24T09:30:00.0000000Z' AS DateTimeValue,
                '11111111-1111-1111-1111-111111111111' AS GuidValue,
                NULL AS NullValue
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one typed field value row.");
        }

        AssertEqual(true, reader.GetFieldValue<bool>(reader.GetOrdinal("BoolValue")), "bool");
        AssertEqual((byte)255, reader.GetFieldValue<byte>(reader.GetOrdinal("ByteValue")), "byte");
        AssertEqual((sbyte)-8, reader.GetFieldValue<sbyte>(reader.GetOrdinal("SByteValue")), "sbyte");
        AssertEqual((short)32000, reader.GetFieldValue<short>(reader.GetOrdinal("ShortValue")), "short");
        AssertEqual((ushort)65000, reader.GetFieldValue<ushort>(reader.GetOrdinal("UShortValue")), "ushort");
        AssertEqual(123456, reader.GetFieldValue<int>(reader.GetOrdinal("IntValue")), "int");
        AssertEqual(4000000000U, reader.GetFieldValue<uint>(reader.GetOrdinal("UIntValue")), "uint");
        AssertEqual(9007199254740991L, reader.GetFieldValue<long>(reader.GetOrdinal("LongValue")), "long");
        AssertEqual(42UL, reader.GetFieldValue<ulong>(reader.GetOrdinal("ULongValue")), "ulong");

        AssertNear(12.5f, reader.GetFieldValue<float>(reader.GetOrdinal("FloatValue")), 0.0001f, "float");
        AssertNear(99.125, reader.GetFieldValue<double>(reader.GetOrdinal("DoubleValue")), 0.0001, "double");
        AssertEqual(123456.789m, reader.GetFieldValue<decimal>(reader.GetOrdinal("DecimalValue")), "decimal");

        var expectedDateTime = DateTime.Parse(
            "2026-05-24T09:30:00.0000000Z",
            null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        AssertEqual(expectedDateTime, reader.GetFieldValue<DateTime>(reader.GetOrdinal("DateTimeValue")), "DateTime");
        AssertEqual(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            reader.GetFieldValue<Guid>(reader.GetOrdinal("GuidValue")),
            "Guid");

        if (reader.GetFieldValue<string?>(reader.GetOrdinal("NullValue")) is not null)
        {
            throw new InvalidOperationException("Nullable typed field value did not return null.");
        }

        try
        {
            reader.GetFieldValue<int>(reader.GetOrdinal("NullValue"));
        }
        catch (InvalidCastException)
        {
            return "OK";
        }

        throw new InvalidOperationException("Null value converted to non-nullable int.");
    }

    private static void AssertEqual<T>(T expected, T actual, string operation)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected {expected}, got {actual}.");
        }
    }

    private static void AssertNear(float expected, float actual, float tolerance, string operation)
    {
        if (Math.Abs(actual - expected) > tolerance)
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected {expected}, got {actual}.");
        }
    }

    private static void AssertNear(double expected, double actual, double tolerance, string operation)
    {
        if (Math.Abs(actual - expected) > tolerance)
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected {expected}, got {actual}.");
        }
    }
}
