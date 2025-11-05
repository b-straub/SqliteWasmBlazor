using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Relationships;

/// <summary>
/// Test creating Todo with binary(16) foreign key to TodoList
/// </summary>
internal class TodoCreateWithForeignKeyTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Todo_CreateWithForeignKey";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Create parent TodoList
        var listId = Guid.NewGuid();
        var list = new TodoList
        {
            Id = listId,
            Title = "Work Tasks",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.TodoLists.Add(list);
        await context.SaveChangesAsync();

        // Create child Todo
        var todoId = Guid.NewGuid();
        var todo = new Todo
        {
            Id = todoId,
            Title = "Complete project",
            Description = "Finish the WASM project",
            TodoListId = listId,
            Priority = 1,
            DueDate = DateTime.UtcNow.AddDays(7)
        };

        context.Todos.Add(todo);
        await context.SaveChangesAsync();

        // Verify: Read back and check
        var retrieved = await context.Todos.FindAsync(todoId);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Todo not found after insert");
        }

        if (retrieved.TodoListId != listId)
        {
            throw new InvalidOperationException($"Foreign key mismatch: expected {listId}, got {retrieved.TodoListId}");
        }

        if (retrieved.Title != "Complete project")
        {
            throw new InvalidOperationException($"Title mismatch");
        }

        return "OK";
    }
}
