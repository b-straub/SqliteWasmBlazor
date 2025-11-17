using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Checkpoints;

/// <summary>
/// Tests checkpoint restoration followed by delta reapplication.
/// Scenario: Restore to earlier checkpoint, then reapply changes from a delta export.
/// This simulates recovering from a bad state while preserving valid changes from another device.
/// </summary>
internal class RestoreToCheckpointWithDeltaReapplyTest(IDbContextFactory<TodoDbContext> factory) : SqliteWasmTest(factory)
{
    public override string Name => "RestoreToCheckpoint_WithDeltaReapply";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var dbContext = await Factory.CreateDbContextAsync();

        // Step 1: Create initial items
        var item1 = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Original item",
            Description = "Before checkpoint",
            UpdatedAt = DateTime.UtcNow,
            IsCompleted = false
        };

        dbContext.TodoItems.Add(item1);
        await dbContext.SaveChangesAsync();
        await Task.Delay(100);

        // Step 2: Create checkpoint1
        var checkpoint1 = new SyncState
        {
            CreatedAt = DateTime.UtcNow,
            Description = "Checkpoint before bad changes",
            ActiveItemCount = 1,
            TombstoneCount = 0,
            CheckpointType = "Manual"
        };

        dbContext.SyncState.Add(checkpoint1);
        await dbContext.SaveChangesAsync();
        await Task.Delay(100);

        // Step 3: Create "bad" item that we'll want to remove
        var badItem = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Bad item to rollback",
            Description = "This was a mistake",
            UpdatedAt = DateTime.UtcNow,
            IsCompleted = false
        };

        dbContext.TodoItems.Add(badItem);
        await dbContext.SaveChangesAsync();

        // Step 4: Simulate having a delta export with good changes (from another device)
        // These are changes that happened after checkpoint1 but are valid
        var goodDeltaItem = new TodoItemDto
        {
            Id = Guid.NewGuid(),
            Title = "Good delta item",
            Description = "Valid change from another device",
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5), // After checkpoint but before bad item
            IsCompleted = true,
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
            IsDeleted = false,
            DeletedAt = null
        };

        // Verify we have 2 items (original + bad)
        var countBefore = await dbContext.TodoItems.CountAsync();
        if (countBefore != 2)
        {
            throw new InvalidOperationException($"Expected 2 items before restore, got {countBefore}");
        }

        // Step 5: Restore to checkpoint1 (removes bad item)
        var itemsToDelete = await dbContext.TodoItems
            .Where(t => t.UpdatedAt > checkpoint1.CreatedAt)
            .ToListAsync();

        dbContext.TodoItems.RemoveRange(itemsToDelete);
        await dbContext.SaveChangesAsync();

        // Verify we're back to 1 item
        var countAfterRestore = await dbContext.TodoItems.CountAsync();
        if (countAfterRestore != 1)
        {
            throw new InvalidOperationException($"Expected 1 item after restore, got {countAfterRestore}");
        }

        // Step 6: Reapply good delta changes
        var deltaEntity = goodDeltaItem.ToEntity();
        dbContext.TodoItems.Add(deltaEntity);
        await dbContext.SaveChangesAsync();

        // Verify final state
        var finalCount = await dbContext.TodoItems.CountAsync();
        if (finalCount != 2)
        {
            throw new InvalidOperationException($"Expected 2 items after delta reapply, got {finalCount}");
        }

        // Verify correct items exist
        var originalExists = await dbContext.TodoItems.AnyAsync(t => t.Id == item1.Id);
        var goodDeltaExists = await dbContext.TodoItems.AnyAsync(t => t.Id == goodDeltaItem.Id);
        var badItemExists = await dbContext.TodoItems.AnyAsync(t => t.Id == badItem.Id);

        if (!originalExists)
        {
            throw new InvalidOperationException("Original item should still exist");
        }

        if (!goodDeltaExists)
        {
            throw new InvalidOperationException("Good delta item should be reapplied");
        }

        if (badItemExists)
        {
            throw new InvalidOperationException("Bad item should not exist after restore");
        }

        // Verify good delta item properties
        var reappliedItem = await dbContext.TodoItems.FirstAsync(t => t.Id == goodDeltaItem.Id);
        if (reappliedItem.Title != "Good delta item")
        {
            throw new InvalidOperationException("Reapplied item title mismatch");
        }

        if (!reappliedItem.IsCompleted)
        {
            throw new InvalidOperationException("Reapplied item should be completed");
        }

        if (!reappliedItem.CompletedAt.HasValue)
        {
            throw new InvalidOperationException("Reapplied item should have CompletedAt");
        }

        return "OK";
    }
}
