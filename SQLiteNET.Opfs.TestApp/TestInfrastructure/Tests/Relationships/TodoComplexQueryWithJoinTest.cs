using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Relationships;

/// <summary>
/// Test complex queries with Join across TodoList and Todo tables using binary(16) Guid keys
/// </summary>
internal class TodoComplexQueryWithJoinTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Todo_ComplexQueryWithJoin";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Create multiple TodoLists
        var activeListId = Guid.NewGuid();
        var inactiveListId = Guid.NewGuid();

        var activeList = new TodoList
        {
            Id = activeListId,
            Title = "Active List",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var inactiveList = new TodoList
        {
            Id = inactiveListId,
            Title = "Inactive List",
            IsActive = false,
            CreatedAt = DateTime.UtcNow
        };

        context.TodoLists.AddRange(activeList, inactiveList);
        await context.SaveChangesAsync();

        // Create Todos in active list (some completed, some not)
        var activeTodo1 = new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Active Todo 1",
            TodoListId = activeListId,
            Completed = false,
            Priority = 1
        };

        var activeTodo2 = new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Active Todo 2",
            TodoListId = activeListId,
            Completed = true,
            CompletedAt = DateTime.UtcNow,
            Priority = 2
        };

        var activeTodo3 = new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Active Todo 3",
            TodoListId = activeListId,
            Completed = false,
            Priority = 3,
            DueDate = DateTime.UtcNow.AddDays(-1) // Overdue
        };

        // Create Todos in inactive list
        var inactiveTodo1 = new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Inactive Todo 1",
            TodoListId = inactiveListId,
            Completed = false,
            Priority = 1
        };

        context.Todos.AddRange(activeTodo1, activeTodo2, activeTodo3, inactiveTodo1);
        await context.SaveChangesAsync();

        // Test 1: Query incomplete todos from active lists only
        var incompleteTodosInActiveLists = await context.Todos
            .Include(t => t.TodoList)
            .Where(t => t.TodoList!.IsActive && !t.Completed)
            .ToListAsync();

        if (incompleteTodosInActiveLists.Count != 2)
        {
            throw new InvalidOperationException($"Expected 2 incomplete todos in active lists, got {incompleteTodosInActiveLists.Count}");
        }

        // Test 2: Query overdue todos (DueDate < now and not completed)
        var overdueTodos = await context.Todos
            .Where(t => t.DueDate.HasValue && t.DueDate < DateTime.UtcNow && !t.Completed)
            .ToListAsync();

        if (overdueTodos.Count != 1)
        {
            throw new InvalidOperationException($"Expected 1 overdue todo, got {overdueTodos.Count}");
        }

        if (overdueTodos[0].Title != "Active Todo 3")
        {
            throw new InvalidOperationException($"Expected overdue todo to be 'Active Todo 3', got '{overdueTodos[0].Title}'");
        }

        // Test 3: Query TodoLists with count of completed todos
        var listsWithCompletedCount = await context.TodoLists
            .Select(l => new
            {
                List = l,
                CompletedCount = l.Todos.Count(t => t.Completed),
                TotalCount = l.Todos.Count
            })
            .Where(x => x.List.IsActive)
            .FirstOrDefaultAsync();

        if (listsWithCompletedCount is null)
        {
            throw new InvalidOperationException("No active list found");
        }

        if (listsWithCompletedCount.CompletedCount != 1)
        {
            throw new InvalidOperationException($"Expected 1 completed todo in active list, got {listsWithCompletedCount.CompletedCount}");
        }

        if (listsWithCompletedCount.TotalCount != 3)
        {
            throw new InvalidOperationException($"Expected 3 total todos in active list, got {listsWithCompletedCount.TotalCount}");
        }

        // Test 4: Query todos ordered by priority with list info
        var todosByPriority = await context.Todos
            .Include(t => t.TodoList)
            .Where(t => t.TodoList!.IsActive)
            .OrderBy(t => t.Priority)
            .Select(t => new { t.Title, t.Priority, ListTitle = t.TodoList!.Title })
            .ToListAsync();

        if (todosByPriority.Count != 3)
        {
            throw new InvalidOperationException($"Expected 3 todos ordered by priority, got {todosByPriority.Count}");
        }

        if (todosByPriority[0].Priority != 1 || todosByPriority[0].Title != "Active Todo 1")
        {
            throw new InvalidOperationException("Priority ordering incorrect");
        }

        return "OK";
    }
}
