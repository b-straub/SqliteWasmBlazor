using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

internal class BulkInsert100EntitiesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "BulkInsert_100Entities";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var items = Enumerable.Range(1, 100)
            .Select(i => new TodoItem
            {
                Title = $"Item {i}",
                Description = $"Description {i}",
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        context.TodoItems.AddRange(items);
        await context.SaveChangesAsync();

        var count = await context.TodoItems.CountAsync();
        if (count < 100)
        {
            throw new InvalidOperationException($"Expected at least 100 items, got {count}");
        }

        return "OK";
    }
}
