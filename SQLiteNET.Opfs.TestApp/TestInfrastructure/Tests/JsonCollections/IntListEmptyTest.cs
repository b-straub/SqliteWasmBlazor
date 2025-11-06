using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.JsonCollections;

internal class IntListEmptyTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "IntList_Empty";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entity = new TypeTestEntity { IntList = new List<int>() };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (retrieved.IntList.Count != 0)
        {
            throw new InvalidOperationException("IntList should be empty");
        }

        return "OK";
    }
}
