using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.RaceConditions;

/// <summary>
/// Tests that with PRAGMA synchronous = FULL, purge-then-load operations work correctly.
///
/// This test was originally designed to reproduce a race condition that occurred with
/// synchronous = NORMAL (default), where xSync() was not called after implicit transactions.
///
/// With synchronous = FULL:
/// - xSync() is called after EVERY transaction (including implicit ones from SaveChangesAsync)
/// - DELETE operations are guaranteed to flush before returning
/// - INSERT operations with overlapping primary keys work correctly
///
/// Expected Behavior:
/// - All operations complete successfully without constraint violations
/// - No need for explicit transactions or manual flush coordination
///
/// Reproduces the exact scenario from WebAppBase full sync:
/// - PurgeTableDataAsync() deletes all TodoLists
/// - LoadAsync() immediately inserts new TodoLists from server with overlapping IDs
/// </summary>
internal class PurgeThenLoadRaceConditionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "RaceCondition_PurgeThenLoad";

    public override async ValueTask<string?> RunTestAsync()
    {
        // Phase 1: Setup - Create initial TodoLists with known GUIDs
        var initialIds = new[]
        {
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333")
        };

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var initialLists = initialIds.Select(id => new TodoList
            {
                Id = id,
                Title = $"Initial List {id}",
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            }).ToList();

            context.TodoLists.AddRange(initialLists);
            await context.SaveChangesAsync();
        }

        // Verify initial data was persisted
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoLists.CountAsync();
            if (count != 3)
            {
                throw new InvalidOperationException($"Setup failed: Expected 3 TodoLists, got {count}");
            }
        }

        // Phase 2: Simulate Full Sync - Purge then immediately load new data
        // With synchronous = FULL, this should work without explicit transactions
        await using (var context = await Factory.CreateDbContextAsync())
        {
            // Step 1: PURGE - Delete all existing TodoLists (simulates PurgeTableDataAsync)
            // Must materialize the query to load entities before deleting
            var existingLists = await context.TodoLists.ToListAsync();
            context.TodoLists.RemoveRange(existingLists);
            await context.SaveChangesAsync(); // With synchronous=FULL, xSync() is called here

            // Clear change tracker to remove deleted entities from memory
            context.ChangeTracker.Clear();

            Console.WriteLine("Purged all data from table: todoLists (xSync called automatically)");

            // Step 2: LOAD - Immediately insert new TodoLists with OVERLAPPING IDs
            // This simulates server data that happens to have the same GUIDs
            var newLists = new[]
            {
                new TodoList
                {
                    Id = initialIds[0], // Same GUID as deleted entity!
                    Title = "New List From Server 1",
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                },
                new TodoList
                {
                    Id = initialIds[1], // Same GUID as deleted entity!
                    Title = "New List From Server 2",
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                },
                new TodoList
                {
                    Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), // New GUID
                    Title = "New List From Server 3",
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                }
            };

            context.TodoLists.AddRange(newLists);

            // With synchronous=FULL, this should succeed without UNIQUE constraint errors
            await context.SaveChangesAsync();

            Console.WriteLine("Successfully loaded new data immediately after purge");
        }

        // Phase 3: Verification
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var finalCount = await context.TodoLists.CountAsync();
            if (finalCount != 3)
            {
                throw new InvalidOperationException($"Verification failed: Expected 3 TodoLists, got {finalCount}");
            }

            // Verify we have the NEW data (not the old data)
            var newList1 = await context.TodoLists.FindAsync(initialIds[0]);
            if (newList1 is null || !newList1.Title.Contains("From Server"))
            {
                throw new InvalidOperationException("Verification failed: Expected new data, got old data or null");
            }

            var newList2 = await context.TodoLists.FindAsync(initialIds[1]);
            if (newList2 is null || !newList2.Title.Contains("From Server"))
            {
                throw new InvalidOperationException("Verification failed: Expected new data, got old data or null");
            }

            Console.WriteLine($"Verification passed: Found 3 new TodoLists with correct titles");
        }

        Console.WriteLine("✅ SUCCESS - Purge-then-load completed successfully with synchronous=FULL");
        return "OK";
    }
}
