using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.RaceConditions;

/// <summary>
/// Tests purge-then-load operations using explicit transactions.
///
/// With PRAGMA synchronous = FULL, explicit transactions are NO LONGER REQUIRED
/// to prevent race conditions, but they remain a valid pattern for grouping
/// multiple operations into a single atomic unit.
///
/// This test demonstrates that explicit transactions still work correctly:
/// 1. Wrap DELETE + INSERT operations in a single transaction
/// 2. CommitAsync() calls xSync() (same as implicit transactions with synchronous=FULL)
/// 3. All operations complete successfully
///
/// Expected Behavior:
/// - All operations complete successfully without constraint violations
/// - Transaction provides atomicity (all-or-nothing semantics)
/// - Functionally equivalent to separate SaveChangesAsync calls with synchronous=FULL
///
/// Note: This pattern is optional with synchronous=FULL, but may still be useful
/// for ensuring atomicity across multiple table operations.
/// </summary>
internal class PurgeThenLoadWithTransactionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "RaceCondition_PurgeThenLoadWithTransaction";

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

        // Phase 2: Simulate Full Sync with TRANSACTION - Purge then immediately load new data
        // This is the FIXED approach that prevents the race condition
        await using (var context = await Factory.CreateDbContextAsync())
        {
            // Begin transaction BEFORE purge
            await using var transaction = await context.Database.BeginTransactionAsync();

            // Step 1: PURGE - Delete all existing TodoLists
            // Must materialize the query to load entities before deleting
            var existingLists = await context.TodoLists.ToListAsync();
            context.TodoLists.RemoveRange(existingLists);
            await context.SaveChangesAsync();

            // Clear change tracker to remove deleted entities from memory
            context.ChangeTracker.Clear();

            Console.WriteLine("Purged all data from table: todoLists (within transaction)");

            // Step 2: LOAD - Immediately insert new TodoLists with OVERLAPPING IDs
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
            await context.SaveChangesAsync();

            Console.WriteLine("Loaded new data immediately after purge (within transaction)");

            // Step 3: Commit transaction
            await transaction.CommitAsync();

            Console.WriteLine("Transaction committed - all operations atomic");
        }

        // Phase 3: Verification - Ensure the new data was persisted correctly
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

        Console.WriteLine("✅ SUCCESS - Explicit transaction pattern verified (optional with synchronous=FULL)");
        return "OK";
    }
}
