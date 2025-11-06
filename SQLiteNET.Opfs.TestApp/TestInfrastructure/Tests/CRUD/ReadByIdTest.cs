using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.CRUD;

internal class ReadByIdTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Read_ById";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var item = new TodoItem
        {
            Title = "Findable Todo",
            Description = "Test",
            CreatedAt = DateTime.UtcNow
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
