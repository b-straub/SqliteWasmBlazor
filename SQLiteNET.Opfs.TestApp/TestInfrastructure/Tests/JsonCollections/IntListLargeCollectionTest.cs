using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.JsonCollections;

internal class IntListLargeCollectionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "IntList_LargeCollection";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var largeList = Enumerable.Range(1, 1000).ToList();
        var entity = new TypeTestEntity { IntList = largeList };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (retrieved.IntList.Count != 1000)
        {
            throw new InvalidOperationException("IntList count mismatch");
        }

        if (!retrieved.IntList.SequenceEqual(largeList))
        {
            throw new InvalidOperationException("IntList content mismatch");
        }

        return "OK";
    }
}
