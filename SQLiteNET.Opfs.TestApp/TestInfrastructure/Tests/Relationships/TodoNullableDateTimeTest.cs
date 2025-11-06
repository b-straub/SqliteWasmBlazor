using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Relationships;

/// <summary>
/// Test nullable DateTime fields (DueDate, CompletedAt) with binary(16) Guid keys
/// </summary>
internal class TodoNullableDateTimeTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Todo_NullableDateTime";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Create TodoList
        var listId = Guid.NewGuid();
        var list = new TodoList
        {
            Id = listId,
            Title = "DateTime Test List",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.TodoLists.Add(list);
        await context.SaveChangesAsync();

        // Test 1: Todo with no DueDate and no CompletedAt
        var todo1Id = Guid.NewGuid();
        var todo1 = new Todo
        {
            Id = todo1Id,
            Title = "No dates set",
            TodoListId = listId,
            DueDate = null,
            Completed = false,
            CompletedAt = null
        };
        context.Todos.Add(todo1);
        await context.SaveChangesAsync();

        var retrieved1 = await context.Todos.FindAsync(todo1Id);
        if (retrieved1 is null)
        {
            throw new InvalidOperationException("Todo1 not found");
        }

        if (retrieved1.DueDate.HasValue)
        {
            throw new InvalidOperationException("DueDate should be null");
        }

        if (retrieved1.CompletedAt.HasValue)
        {
            throw new InvalidOperationException("CompletedAt should be null");
        }

        // Test 2: Todo with DueDate but no CompletedAt
        var todo2Id = Guid.NewGuid();
        var dueDate = DateTime.UtcNow.AddDays(7);
        var todo2 = new Todo
        {
            Id = todo2Id,
            Title = "Has due date",
            TodoListId = listId,
            DueDate = dueDate,
            Completed = false,
            CompletedAt = null
        };
        context.Todos.Add(todo2);
        await context.SaveChangesAsync();

        var retrieved2 = await context.Todos.FindAsync(todo2Id);
        if (retrieved2 is null)
        {
            throw new InvalidOperationException("Todo2 not found");
        }

        if (!retrieved2.DueDate.HasValue)
        {
            throw new InvalidOperationException("DueDate should have value");
        }

        if (Math.Abs((retrieved2.DueDate.Value - dueDate).TotalSeconds) > 1)
        {
            throw new InvalidOperationException($"DueDate mismatch: expected {dueDate}, got {retrieved2.DueDate.Value}");
        }

        if (retrieved2.CompletedAt.HasValue)
        {
            throw new InvalidOperationException("CompletedAt should still be null");
        }

        // Test 3: Todo with both DueDate and CompletedAt (completed task)
        var todo3Id = Guid.NewGuid();
        var completedAt = DateTime.UtcNow;
        var todo3 = new Todo
        {
            Id = todo3Id,
            Title = "Completed task",
            TodoListId = listId,
            DueDate = dueDate,
            Completed = true,
            CompletedAt = completedAt
        };
        context.Todos.Add(todo3);
        await context.SaveChangesAsync();

        var retrieved3 = await context.Todos.FindAsync(todo3Id);
        if (retrieved3 is null)
        {
            throw new InvalidOperationException("Todo3 not found");
        }

        if (!retrieved3.CompletedAt.HasValue)
        {
            throw new InvalidOperationException("CompletedAt should have value");
        }

        if (Math.Abs((retrieved3.CompletedAt.Value - completedAt).TotalSeconds) > 1)
        {
            throw new InvalidOperationException($"CompletedAt mismatch");
        }

        // Test 4: Update null to value and vice versa
        retrieved1.DueDate = DateTime.UtcNow.AddDays(3);
        retrieved1.Completed = true;
        retrieved1.CompletedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var updated1 = await context.Todos.FindAsync(todo1Id);
        if (updated1 is null || !updated1.DueDate.HasValue || !updated1.CompletedAt.HasValue)
        {
            throw new InvalidOperationException("Failed to update null values to non-null");
        }

        // Update back to null
        updated1.DueDate = null;
        updated1.CompletedAt = null;
        await context.SaveChangesAsync();

        var updated1Again = await context.Todos.FindAsync(todo1Id);
        if (updated1Again is null || updated1Again.DueDate.HasValue || updated1Again.CompletedAt.HasValue)
        {
            throw new InvalidOperationException("Failed to update non-null values back to null");
        }

        return "OK";
    }
}
