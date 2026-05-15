using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

internal class DeleteSingleEntityTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Delete_SingleEntity";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var item = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "To Delete",
            Description = "Test",
            UpdatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(item);
        await context.SaveChangesAsync();

        var id = item.Id;

        context.TodoItems.Remove(item);
        await context.SaveChangesAsync();

        var deleted = await context.TodoItems.FindAsync(id);
        if (deleted is not null)
        {
            throw new InvalidOperationException("Entity was not deleted");
        }

        return "OK";
    }
}
