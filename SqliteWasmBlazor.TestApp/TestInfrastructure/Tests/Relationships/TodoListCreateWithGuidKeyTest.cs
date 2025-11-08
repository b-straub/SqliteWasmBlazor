using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Relationships;

/// <summary>
/// Test creating TodoList with binary(16) Guid key
/// </summary>
internal class TodoListCreateWithGuidKeyTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "TodoList_CreateWithGuidKey";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var listId = Guid.NewGuid();
        var list = new TodoList
        {
            Id = listId,
            Title = "Shopping List",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        context.TodoLists.Add(list);
        await context.SaveChangesAsync();

        // Verify: Read back and check
        var retrieved = await context.TodoLists.FindAsync(listId);
        if (retrieved is null)
        {
            throw new InvalidOperationException("TodoList not found after insert");
        }

        if (retrieved.Id != listId)
        {
            throw new InvalidOperationException($"ID mismatch: expected {listId}, got {retrieved.Id}");
        }

        if (retrieved.Title != "Shopping List")
        {
            throw new InvalidOperationException($"Title mismatch: expected 'Shopping List', got '{retrieved.Title}'");
        }

        return "OK";
    }
}
