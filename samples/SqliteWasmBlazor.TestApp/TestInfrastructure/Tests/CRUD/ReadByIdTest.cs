using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

internal class ReadByIdTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Read_ById";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var item = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Findable Todo",
            Description = "Test",
            UpdatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(item);
        await context.SaveChangesAsync();

        var found = await context.TodoItems.FindAsync(item.Id);
        if (found is null)
        {
            throw new InvalidOperationException("Failed to find entity");
        }

        if (found.Title != "Findable Todo")
        {
            throw new InvalidOperationException("Title mismatch");
        }

        return "OK";
    }
}
