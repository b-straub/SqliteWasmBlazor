using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

internal class CreateSingleEntityTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Create_SingleEntity";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var item = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Test Todo",
            Description = "Test Description",
            IsCompleted = false,
            UpdatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(item);
        await context.SaveChangesAsync();

        if (item.Id == Guid.Empty)
        {
            throw new InvalidOperationException("ID was not generated");
        }

        return "OK";
    }
}
