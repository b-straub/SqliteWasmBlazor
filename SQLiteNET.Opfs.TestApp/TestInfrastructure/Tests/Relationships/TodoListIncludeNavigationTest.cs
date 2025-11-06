using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Relationships;

/// <summary>
/// Test Include() for loading TodoList with its related Todos (one-to-many relationship)
/// </summary>
internal class TodoListIncludeNavigationTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "TodoList_IncludeNavigation";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Create parent TodoList
        var listId = Guid.NewGuid();
        var list = new TodoList
        {
            Id = listId,
            Title = "Project Tasks",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.TodoLists.Add(list);
        await context.SaveChangesAsync();

        // Create multiple child Todos
        var todo1 = new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Task 1",
            Description = "First task",
            TodoListId = listId,
            Priority = 1
        };

        var todo2 = new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Task 2",
            Description = "Second task",
            TodoListId = listId,
            Priority = 2,
            Completed = true,
            CompletedAt = DateTime.UtcNow
        };

        var todo3 = new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Task 3",
            Description = "Third task",
            TodoListId = listId,
            Priority = 3,
            DueDate = DateTime.UtcNow.AddDays(3)
        };

        context.Todos.AddRange(todo1, todo2, todo3);
        await context.SaveChangesAsync();

        // Test Include: Load TodoList with related Todos
        var retrievedList = await context.TodoLists
            .Include(l => l.Todos)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (retrievedList is null)
        {
            throw new InvalidOperationException("TodoList not found");
        }

        if (retrievedList.Todos.Count != 3)
        {
            throw new InvalidOperationException($"Expected 3 todos, got {retrievedList.Todos.Count}");
        }

        // Verify: Check collection navigation property
        var completedTodos = retrievedList.Todos.Count(t => t.Completed);
        if (completedTodos != 1)
        {
            throw new InvalidOperationException($"Expected 1 completed todo, got {completedTodos}");
        }

        var todosWithDueDate = retrievedList.Todos.Count(t => t.DueDate.HasValue);
        if (todosWithDueDate != 1)
        {
            throw new InvalidOperationException($"Expected 1 todo with due date, got {todosWithDueDate}");
        }

        return "OK";
    }
}
