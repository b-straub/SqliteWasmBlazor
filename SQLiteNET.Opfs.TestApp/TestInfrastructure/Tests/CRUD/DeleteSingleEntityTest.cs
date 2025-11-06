using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.CRUD;

internal class DeleteSingleEntityTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Delete_SingleEntity";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var item = new TodoItem
        {
            Title = "To Delete",
            Description = "Test",
            CreatedAt = DateTime.UtcNow
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
