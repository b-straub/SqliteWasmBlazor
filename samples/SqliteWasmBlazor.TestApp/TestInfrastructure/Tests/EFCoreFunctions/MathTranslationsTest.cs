using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class MathTranslationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_MathTranslations";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        context.TypeTests.AddRange(
            CreateRow(1, -7, -2, 2.25),
            CreateRow(2, 4, 9, 3.60),
            CreateRow(3, 12, 5, 4.00),
            CreateRow(4, 1, 0, 0.50));
        await context.SaveChangesAsync();

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Abs(e.IntValue) == 7)
                .Select(e => e.Id),
            [1],
            "Math.Abs");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Max(e.IntValue, e.ShortValue) == 9)
                .Select(e => e.Id),
            [2],
            "Math.Max");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Min(e.IntValue, e.ShortValue) == 5)
                .Select(e => e.Id),
            [3],
            "Math.Min");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Round(e.DoubleValue) == 4.0)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [2, 3],
            "Math.Round");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Round(e.DoubleValue, 1) == 3.6)
                .Select(e => e.Id),
            [2],
            "Math.Round digits");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Ceiling(e.DoubleValue) == 3.0)
                .Select(e => e.Id),
            [1],
            "Math.Ceiling");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Floor(e.DoubleValue) == 3.0)
                .Select(e => e.Id),
            [2],
            "Math.Floor");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Pow(e.DoubleValue, 2.0) == 16.0)
                .Select(e => e.Id),
            [3],
            "Math.Pow");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => Math.Sqrt(e.DoubleValue) == 2.0)
                .Select(e => e.Id),
            [3],
            "Math.Sqrt");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.DoubleValue % 2.0 == 0.0)
                .Select(e => e.Id),
            [3],
            "double modulo");

        var advanced = await context.TypeTests
            .Where(e => e.Id == 4)
            .Select(e => new
            {
                Acos = Math.Acos(e.DoubleValue),
                Acosh = Math.Acosh(e.DoubleValue + 1.0),
                Asin = Math.Asin(e.DoubleValue),
                Asinh = Math.Asinh(e.DoubleValue),
                Atan = Math.Atan(e.DoubleValue),
                Atan2 = Math.Atan2(e.DoubleValue, 2.0),
                Atanh = Math.Atanh(e.DoubleValue / 2.0),
                Cos = Math.Cos(e.DoubleValue),
                Cosh = Math.Cosh(e.DoubleValue),
                Exp = Math.Exp(e.DoubleValue),
                Log = Math.Log(e.DoubleValue),
                LogBase = Math.Log(e.DoubleValue, 2.0),
                Log2 = Math.Log2(e.DoubleValue),
                Log10 = Math.Log10(e.DoubleValue),
                Sign = Math.Sign(e.IntValue),
                Sin = Math.Sin(e.DoubleValue),
                Sinh = Math.Sinh(e.DoubleValue),
                Tan = Math.Tan(e.DoubleValue),
                Tanh = Math.Tanh(e.DoubleValue),
                Truncate = Math.Truncate(e.DoubleValue),
                Radians = double.DegreesToRadians(e.DoubleValue),
                Degrees = double.RadiansToDegrees(e.DoubleValue),
                GenericSin = double.Sin(e.DoubleValue),
                MathFSin = MathF.Sin(e.FloatValue),
                FloatSin = float.Sin(e.FloatValue)
            })
            .SingleAsync();

        const double value = 0.5;
        AssertClose(Math.Acos(value), advanced.Acos, "Math.Acos");
        AssertClose(Math.Acosh(value + 1.0), advanced.Acosh, "Math.Acosh");
        AssertClose(Math.Asin(value), advanced.Asin, "Math.Asin");
        AssertClose(Math.Asinh(value), advanced.Asinh, "Math.Asinh");
        AssertClose(Math.Atan(value), advanced.Atan, "Math.Atan");
        AssertClose(Math.Atan2(value, 2.0), advanced.Atan2, "Math.Atan2");
        AssertClose(Math.Atanh(value / 2.0), advanced.Atanh, "Math.Atanh");
        AssertClose(Math.Cos(value), advanced.Cos, "Math.Cos");
        AssertClose(Math.Cosh(value), advanced.Cosh, "Math.Cosh");
        AssertClose(Math.Exp(value), advanced.Exp, "Math.Exp");
        AssertClose(Math.Log(value), advanced.Log, "Math.Log");
        AssertClose(Math.Log(value, 2.0), advanced.LogBase, "Math.Log base");
        AssertClose(Math.Log2(value), advanced.Log2, "Math.Log2");
        AssertClose(Math.Log10(value), advanced.Log10, "Math.Log10");
        AssertClose(Math.Sin(value), advanced.Sin, "Math.Sin");
        AssertClose(Math.Sinh(value), advanced.Sinh, "Math.Sinh");
        AssertClose(Math.Tan(value), advanced.Tan, "Math.Tan");
        AssertClose(Math.Tanh(value), advanced.Tanh, "Math.Tanh");
        AssertClose(Math.Truncate(value), advanced.Truncate, "Math.Truncate");
        AssertClose(double.DegreesToRadians(value), advanced.Radians, "double.DegreesToRadians");
        AssertClose(double.RadiansToDegrees(value), advanced.Degrees, "double.RadiansToDegrees");
        AssertClose(double.Sin(value), advanced.GenericSin, "double.Sin");
        AssertClose(MathF.Sin((float)value), advanced.MathFSin, "MathF.Sin");
        AssertClose(float.Sin((float)value), advanced.FloatSin, "float.Sin");

        if (advanced.Sign != 1)
        {
            throw new InvalidOperationException($"Math.Sign failed: expected 1, got {advanced.Sign}.");
        }

        var random = await context.TypeTests
            .Select(e => EF.Functions.Random())
            .FirstAsync();
        if (random < 0.0 || random >= 1.0)
        {
            throw new InvalidOperationException($"EF.Functions.Random returned out-of-range value {random}.");
        }

        return "OK";
    }

    private static TypeTestEntity CreateRow(int id, int intValue, short shortValue, double doubleValue)
    {
        return new TypeTestEntity
        {
            Id = id,
            IntValue = intValue,
            ShortValue = shortValue,
            DoubleValue = doubleValue,
            FloatValue = (float)doubleValue,
            DecimalValue = (decimal)doubleValue,
            StringValue = $"Math row {id}",
            DateTimeValue = new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
            DateTimeOffsetValue = new DateTimeOffset(2026, 5, 24, 9, 0, 0, TimeSpan.Zero),
            GuidValue = Guid.Parse($"00000000-0000-0000-0000-00000000000{id}"),
            EnumValue = TestEnum.FIRST,
            CharValue = 'M'
        };
    }

    private static async Task AssertSequenceAsync(
        IQueryable<int> query,
        int[] expected,
        string operation)
    {
        var actual = await query.OrderBy(id => id).ToListAsync();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
        }
    }

    private static void AssertClose(double expected, double actual, string operation)
    {
        if (Math.Abs(expected - actual) > 0.0000001)
        {
            throw new InvalidOperationException($"{operation} failed: expected {expected}, got {actual}.");
        }
    }
}
