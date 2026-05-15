using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Checkpoints;

/// <summary>
/// Tests basic checkpoint restoration functionality.
/// Verifies that restoring to a checkpoint removes all items modified after that checkpoint.
/// </summary>
internal class RestoreToCheckpointBasicTest(IDbContextFactory<TodoDbContext> factory) : SqliteWasmTest(factory)
{
    public override string Name => "RestoreToCheckpoint_Basic";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var dbContext = await Factory.CreateDbContextAsync();

        // Step 1: Create initial items
        var item1 = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Item before checkpoint",
            Description = "Created before",
            UpdatedAt = DateTime.UtcNow,
            IsCompleted = false
        };

        dbContext.TodoItems.Add(item1);
        await dbContext.SaveChangesAsync();
        await Task.Delay(100); // Ensure different timestamps

        // Step 2: Create first checkpoint
        var checkpoint1 = new SyncState
        {
            CreatedAt = DateTime.UtcNow,
            Description = "First checkpoint",
            ActiveItemCount = 1,
            TombstoneCount = 0,
            CheckpointType = "Manual"
        };

        dbContext.SyncState.Add(checkpoint1);
        await dbContext.SaveChangesAsync();
        await Task.Delay(100);

        // Step 3: Create items after checkpoint
        var item2 = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Item after checkpoint",
            Description = "Created after",
            UpdatedAt = DateTime.UtcNow,
            IsCompleted = false
        };

        var item3 = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Another item after checkpoint",
            Description = "Also created after",
            UpdatedAt = DateTime.UtcNow,
            IsCompleted = false
        };

        dbContext.TodoItems.AddRange(item2, item3);
        await dbContext.SaveChangesAsync();

        // Verify we have 3 items total
        var countBefore = await dbContext.TodoItems.CountAsync();
        if (countBefore != 3)
        {
            throw new InvalidOperationException($"Expected 3 items before restore, got {countBefore}");
        }

        // Step 4: Restore to checkpoint1
        // This should remove item2 and item3 (created after checkpoint)
        var itemsToDelete = await dbContext.TodoItems
            .Where(t => t.UpdatedAt > checkpoint1.CreatedAt)
            .ToListAsync();

        dbContext.TodoItems.RemoveRange(itemsToDelete);
        await dbContext.SaveChangesAsync();

        // Verify restoration
        var countAfter = await dbContext.TodoItems.CountAsync();
        if (countAfter != 1)
        {
            throw new InvalidOperationException($"Expected 1 item after restore, got {countAfter}");
        }

        var remainingItem = await dbContext.TodoItems.FirstAsync();
        if (remainingItem.Id != item1.Id)
        {
            throw new InvalidOperationException("Wrong item remaining after restore");
        }

        if (remainingItem.Title != "Item before checkpoint")
        {
            throw new InvalidOperationException("Item title mismatch");
        }

        // Verify item2 and item3 were deleted
        var item2Exists = await dbContext.TodoItems.AnyAsync(t => t.Id == item2.Id);
        var item3Exists = await dbContext.TodoItems.AnyAsync(t => t.Id == item3.Id);
        if (item2Exists)
        {
            throw new InvalidOperationException("Item2 should have been deleted");
        }

        if (item3Exists)
        {
            throw new InvalidOperationException("Item3 should have been deleted");
        }

        return "OK";
    }
}
