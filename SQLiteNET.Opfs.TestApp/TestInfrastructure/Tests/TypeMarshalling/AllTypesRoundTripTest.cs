using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.TypeMarshalling;

internal class AllTypesRoundTripTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AllTypes_RoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entity = new TypeTestEntity
        {
            ByteValue = 255,
            ShortValue = -32768,
            IntValue = int.MaxValue,
            LongValue = long.MaxValue,
            FloatValue = 3.14159f,
            DoubleValue = Math.PI,
            DecimalValue = 123456.789m,
            BoolValue = true,
            StringValue = "Test String with émojis 🚀",
            DateTimeValue = DateTime.UtcNow,
            GuidValue = Guid.NewGuid(),
            BlobValue = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD },
            EnumValue = TestEnum.SECOND,
            CharValue = 'A',
            IntList = new List<int> { 1, 2, 3, 42, 100 }
        };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (retrieved.ByteValue != entity.ByteValue)
        {
            throw new InvalidOperationException("ByteValue mismatch");
        }

        if (retrieved.IntValue != entity.IntValue)
        {
            throw new InvalidOperationException("IntValue mismatch");
        }

        if (retrieved.LongValue != entity.LongValue)
        {
            throw new InvalidOperationException("LongValue mismatch");
        }

        if (Math.Abs(retrieved.FloatValue - entity.FloatValue) > 0.0001f)
        {
            throw new InvalidOperationException("FloatValue mismatch");
        }

        if (retrieved.DecimalValue != entity.DecimalValue)
        {
            throw new InvalidOperationException("DecimalValue mismatch");
        }

        if (retrieved.BoolValue != entity.BoolValue)
        {
            throw new InvalidOperationException("BoolValue mismatch");
        }

        if (retrieved.StringValue != entity.StringValue)
        {
            throw new InvalidOperationException("StringValue mismatch");
        }

        if (retrieved.GuidValue != entity.GuidValue)
        {
            throw new InvalidOperationException("GuidValue mismatch");
        }

        if (!retrieved.BlobValue!.SequenceEqual(entity.BlobValue))
        {
            throw new InvalidOperationException("BlobValue mismatch");
        }

        if (retrieved.EnumValue != entity.EnumValue)
        {
            throw new InvalidOperationException("EnumValue mismatch");
        }

        if (retrieved.CharValue != entity.CharValue)
        {
            throw new InvalidOperationException("CharValue mismatch");
        }

        if (!retrieved.IntList.SequenceEqual(entity.IntList))
        {
            throw new InvalidOperationException("IntList mismatch");
        }

        return "OK";
    }
}
