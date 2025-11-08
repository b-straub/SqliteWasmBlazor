using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

internal class IntegerTypesBoundariesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "IntegerTypes_Boundaries";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entity = new TypeTestEntity
        {
            ByteValue = byte.MaxValue,
            ShortValue = short.MinValue,
            IntValue = int.MinValue,
            LongValue = long.MinValue
        };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (retrieved.ByteValue != byte.MaxValue)
        {
            throw new InvalidOperationException("ByteValue boundary mismatch");
        }

        if (retrieved.ShortValue != short.MinValue)
        {
            throw new InvalidOperationException("ShortValue boundary mismatch");
        }

        if (retrieved.IntValue != int.MinValue)
        {
            throw new InvalidOperationException("IntValue boundary mismatch");
        }

        if (retrieved.LongValue != long.MinValue)
        {
            throw new InvalidOperationException("LongValue boundary mismatch");
        }

        return "OK";
    }
}
