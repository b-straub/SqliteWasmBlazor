using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Relationships;

/// <summary>
/// Test cascade delete: deleting TodoList should delete all related Todos
/// </summary>
internal class TodoListCascadeDeleteTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "TodoList_CascadeDelete";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Create parent TodoList
        var listId = Guid.NewGuid();
        var list = new TodoList
        {
            Id = listId,
            Title = "Temporary List",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.TodoLists.Add(list);
        await context.SaveChangesAsync();

        // Create child Todos
        var todoIds = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var todoId = Guid.NewGuid();
            todoIds.Add(todoId);

            var todo = new Todo
            {
                Id = todoId,
                Title = $"Todo {i + 1}",
                Description = $"Description {i + 1}",
                TodoListId = listId,
                Priority = i
            };
            context.Todos.Add(todo);
        }
        await context.SaveChangesAsync();

        // Verify todos exist
        var todosBeforeDelete = await context.Todos
            .Where(t => t.TodoListId == listId)
            .CountAsync();

        if (todosBeforeDelete != 5)
        {
            throw new InvalidOperationException($"Expected 5 todos before delete, got {todosBeforeDelete}");
        }

        // Delete parent TodoList (should cascade delete all Todos)
        context.TodoLists.Remove(list);
        await context.SaveChangesAsync();

        // Verify: TodoList is deleted
        var deletedList = await context.TodoLists.FindAsync(listId);
        if (deletedList is not null)
        {
            throw new InvalidOperationException("TodoList should be deleted");
        }

        // Verify: All related Todos are cascade deleted
        var todosAfterDelete = await context.Todos
            .Where(t => t.TodoListId == listId)
            .CountAsync();

        if (todosAfterDelete != 0)
        {
            throw new InvalidOperationException($"Expected 0 todos after cascade delete, got {todosAfterDelete}");
        }

        // Verify: Individual todo IDs don't exist
        foreach (var todoId in todoIds)
        {
            var deletedTodo = await context.Todos.FindAsync(todoId);
            if (deletedTodo is not null)
            {
                throw new InvalidOperationException($"Todo {todoId} should be deleted");
            }
        }

        return "OK";
    }
}
