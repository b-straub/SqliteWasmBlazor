using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Extensions;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

/// <summary>
/// Tests FTS5 index integrity after soft-deleting items then hard-deleting all rows.
///
/// This test catches the bug where:
/// 1. Items are created (INSERT trigger adds to FTS5)
/// 2. Some items are soft-deleted (UPDATE trigger removes from FTS5)
/// 3. DELETE FROM TodoItems runs (DELETE trigger fires for ALL rows)
/// 4. DELETE trigger tries to remove already-removed soft-deleted items → SQLITE_CORRUPT_VTAB
///
/// The fix: DELETE trigger should have WHEN old.IsDeleted = 0 guard.
/// </summary>
internal class FTS5SoftDeleteThenClearTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "FTS5_SoftDeleteThenClear";

    // FTS5 requires migrations (not EnsureCreated) because the virtual table is created via migration SQL
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Use MigrateAsync to create FTS5 virtual table and triggers
        await context.Database.MigrateAsync();

        // Step 1: Create test items
        var items = new[]
        {
            new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Active Item 1",
                Description = "This item will remain active",
                IsCompleted = false,
                IsDeleted = false,
                UpdatedAt = DateTime.UtcNow
            },
            new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "To Be Soft Deleted",
                Description = "This item will be soft-deleted before clear all",
                IsCompleted = false,
                IsDeleted = false,
                UpdatedAt = DateTime.UtcNow
            },
            new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Also Soft Deleted",
                Description = "Another item to be soft-deleted",
                IsCompleted = false,
                IsDeleted = false,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.TodoItems.AddRange(items);
        await context.SaveChangesAsync();

        // Verify initial FTS5 search works
        var initialResults = await context.SearchTodoItems("soft").ToListAsync();
        if (initialResults.Count != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 results for 'soft' initially, got {initialResults.Count}");
        }

        // Step 2: Soft-delete some items (set IsDeleted = 1)
        // This triggers the UPDATE trigger which removes them from FTS5
        items[1].IsDeleted = true;
        items[1].DeletedAt = DateTime.UtcNow;
        items[2].IsDeleted = true;
        items[2].DeletedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        // Verify soft-deleted items are removed from FTS5 search
        var afterSoftDelete = await context.SearchTodoItems("soft").ToListAsync();
        if (afterSoftDelete.Count != 0)
        {
            throw new InvalidOperationException(
                $"Expected 0 results for 'soft' after soft-delete, got {afterSoftDelete.Count}");
        }

        // Step 3: Hard-delete all rows via raw SQL (like "Clear All" in Administration)
        // This triggers the DELETE trigger for ALL rows, including the soft-deleted ones
        // Bug: DELETE trigger would try to remove already-removed soft-deleted items from FTS5 → corruption
        try
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"DELETE FROM TodoItems failed (FTS5 trigger corruption?): {ex.Message}", ex);
        }

        // Step 4: Verify FTS5 index integrity
        try
        {
            await context.CheckTodoItemFts5IntegrityAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"FTS5 integrity check failed after clear all: {ex.Message}", ex);
        }

        // Step 5: Verify FTS5 rebuild works (this also fails if index is corrupted)
        try
        {
            await context.RebuildTodoItemFts5IndexAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"FTS5 rebuild failed after clear all: {ex.Message}", ex);
        }

        // Step 6: Verify we can still insert and search after clear all
        var newItem = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "New Item After Clear",
            Description = "Fresh item after clearing all data",
            IsCompleted = false,
            IsDeleted = false,
            UpdatedAt = DateTime.UtcNow
        };
        context.TodoItems.Add(newItem);
        await context.SaveChangesAsync();

        var searchAfterClear = await context.SearchTodoItems("fresh").ToListAsync();
        if (searchAfterClear.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 result for 'fresh' after clear+insert, got {searchAfterClear.Count}");
        }

        return "OK";
    }
}
