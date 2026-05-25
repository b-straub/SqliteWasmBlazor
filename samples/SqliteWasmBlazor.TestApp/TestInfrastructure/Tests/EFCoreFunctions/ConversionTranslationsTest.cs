using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class ConversionTranslationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_ConversionTranslations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        context.TypeTests.Add(new TypeTestEntity
        {
            Id = 1,
            BoolValue = true,
            ByteValue = 42,
            ShortValue = -123,
            IntValue = 456,
            LongValue = 9_876_543_210,
            FloatValue = 1.5f,
            DoubleValue = 2.25,
            DecimalValue = 123.45m,
            DateTimeValue = new DateTime(2026, 5, 24, 9, 30, 15, DateTimeKind.Utc),
            DateTimeOffsetValue = new DateTimeOffset(2026, 5, 24, 9, 30, 15, TimeSpan.Zero),
            TimeSpanValue = new TimeSpan(1, 2, 3, 4),
            GuidValue = guid,
            BlobValue = [0x41, 0x42, 0x43],
            EnumValue = TestEnum.SECOND,
            CharValue = 'Z',
            StringValue = "Conversion row"
        });
        await context.SaveChangesAsync();

        var projection = await context.TypeTests
            .Where(e => e.Id == 1)
            .Select(e => new
            {
                Bool = e.BoolValue.ToString(),
                Byte = e.ByteValue.ToString(),
                Short = e.ShortValue.ToString(),
                Int = e.IntValue.ToString(),
                Long = e.LongValue.ToString(),
                Float = e.FloatValue.ToString(),
                Double = e.DoubleValue.ToString(),
                Decimal = e.DecimalValue.ToString(),
                DateTime = e.DateTimeValue.ToString(),
                DateTimeOffset = e.DateTimeOffsetValue.ToString(),
                TimeSpan = e.TimeSpanValue.ToString(),
                Guid = e.GuidValue.ToString(),
                Blob = e.BlobValue!.ToString(),
                Char = e.CharValue.ToString()
            })
            .SingleAsync();

        AssertEqual("True", projection.Bool, "bool.ToString");
        AssertEqual("42", projection.Byte, "byte.ToString");
        AssertEqual("-123", projection.Short, "short.ToString");
        AssertEqual("456", projection.Int, "int.ToString");
        AssertEqual("9876543210", projection.Long, "long.ToString");
        AssertEqual("1.5", projection.Float, "float.ToString");
        AssertEqual("2.25", projection.Double, "double.ToString");
        AssertEqual("123.45", projection.Decimal, "decimal.ToString");
        AssertEqual("2026-05-24T09:30:15.0000000Z", projection.DateTime, "DateTime.ToString");
        AssertEqual("2026-05-24T09:30:15.0000000+00:00", projection.DateTimeOffset, "DateTimeOffset.ToString");
        AssertEqual("1.02:03:04", projection.TimeSpan, "TimeSpan.ToString");
        AssertEqual(guid.ToString(), projection.Guid, "Guid.ToString");
        AssertEqual("ABC", projection.Blob!, "byte[].ToString");
        AssertEqual("Z", projection.Char, "char.ToString");

        return "OK";
    }

    private static void AssertEqual(string expected, string actual, string operation)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected [{expected}], got [{actual}].");
        }
    }
}
