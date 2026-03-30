using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.JsonCollections;

internal class IntListRoundTripTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "IntList_RoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        var entity = new TypeTestEntity
        {
            IntList = new List<int> { 1, 2, 3, 42, 100, -5, 0 }
        };

        await using (var writeCtx = await Factory.CreateDbContextAsync())


        {


            writeCtx.TypeTests.Add(entity);
        await writeCtx.SaveChangesAsync();


        }


        await using var context = await Factory.CreateDbContextAsync();
        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (!retrieved.IntList.SequenceEqual(entity.IntList))
        {
            throw new InvalidOperationException("IntList mismatch");
        }

        return "OK";
    }
}
