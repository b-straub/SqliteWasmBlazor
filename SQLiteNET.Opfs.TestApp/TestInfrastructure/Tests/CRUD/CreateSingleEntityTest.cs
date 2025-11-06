using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.CRUD;

internal class CreateSingleEntityTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Create_SingleEntity";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var item = new TodoItem
        {
            Title = "Test Todo",
            Description = "Test Description",
            IsCompleted = false,
            CreatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(item);
        await context.SaveChangesAsync();

        if (item.Id <= 0)
        {
            throw new InvalidOperationException("ID was not generated");
        }

        return "OK";
    }
}
