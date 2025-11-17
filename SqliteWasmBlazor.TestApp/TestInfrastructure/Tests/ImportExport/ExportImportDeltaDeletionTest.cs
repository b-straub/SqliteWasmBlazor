using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests deletion handling in patch export/import.
/// Demonstrates tracking deletions and applying them to target database.
/// Note: Full sync system would need tombstone table or IsDeleted flag.
/// </summary>
internal class ExportImportDeltaDeletionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_DeltaDeletion";

    public override async ValueTask<string?> RunTestAsync()
    {
        // Step 1: Create initial dataset on source
        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();
        var item3Id = Guid.NewGuid();

        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.AddRange(
                new TodoItem { Id = item1Id, Title = "Item 1", Description = "Keep", IsCompleted = false, UpdatedAt = DateTime.UtcNow },
                new TodoItem { Id = item2Id, Title = "Item 2", Description = "Delete", IsCompleted = false, UpdatedAt = DateTime.UtcNow },
                new TodoItem { Id = item3Id, Title = "Item 3", Description = "Keep", IsCompleted = false, UpdatedAt = DateTime.UtcNow }
            );
            await context.SaveChangesAsync();
        }

        // Step 2: Get snapshot of IDs before deletion
        var beforeDeletionIds = new HashSet<Guid>();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            beforeDeletionIds = (await context.TodoItems.Select(t => t.Id).ToListAsync()).ToHashSet();
        }

        if (beforeDeletionIds.Count != 3)
        {
            throw new InvalidOperationException($"Expected 3 items before deletion, got {beforeDeletionIds.Count}");
        }

        // Step 3: Delete item 2 from source
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var itemToDelete = await context.TodoItems.FirstAsync(t => t.Id == item2Id);
            context.TodoItems.Remove(itemToDelete);
            await context.SaveChangesAsync();
        }

        // Step 4: Calculate deletion patch (items in before snapshot but not in current)
        var afterDeletionIds = new HashSet<Guid>();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            afterDeletionIds = (await context.TodoItems.Select(t => t.Id).ToListAsync()).ToHashSet();
        }

        var deletedIds = beforeDeletionIds.Except(afterDeletionIds).ToList();

        if (deletedIds.Count != 1)
        {
            throw new InvalidOperationException($"Expected 1 deleted item, got {deletedIds.Count}");
        }

        if (!deletedIds.Contains(item2Id))
        {
            throw new InvalidOperationException("Item 2 should be in deleted list");
        }

        // Step 5: Simulate target database with all 3 items (before sync)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
            context.TodoItems.AddRange(
                new TodoItem { Id = item1Id, Title = "Item 1", Description = "Keep", IsCompleted = false, UpdatedAt = DateTime.UtcNow },
                new TodoItem { Id = item2Id, Title = "Item 2", Description = "Delete", IsCompleted = false, UpdatedAt = DateTime.UtcNow },
                new TodoItem { Id = item3Id, Title = "Item 3", Description = "Keep", IsCompleted = false, UpdatedAt = DateTime.UtcNow }
            );
            await context.SaveChangesAsync();
        }

        // Step 6: Apply deletion patch to target
        await using (var context = await Factory.CreateDbContextAsync())
        {
            foreach (var deletedId in deletedIds)
            {
                var itemToDelete = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == deletedId);
                if (itemToDelete is not null)
                {
                    context.TodoItems.Remove(itemToDelete);
                }
            }
            await context.SaveChangesAsync();
        }

        // Step 7: Verify target database has correct items
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 2)
            {
                throw new InvalidOperationException($"Expected 2 items after deletion patch, got {count}");
            }

            var item1Exists = await context.TodoItems.AnyAsync(t => t.Id == item1Id);
            var item2Exists = await context.TodoItems.AnyAsync(t => t.Id == item2Id);
            var item3Exists = await context.TodoItems.AnyAsync(t => t.Id == item3Id);

            if (!item1Exists)
            {
                throw new InvalidOperationException("Item 1 should still exist");
            }

            if (item2Exists)
            {
                throw new InvalidOperationException("Item 2 should be deleted");
            }

            if (!item3Exists)
            {
                throw new InvalidOperationException("Item 3 should still exist");
            }
        }

        return "OK";
    }
}
