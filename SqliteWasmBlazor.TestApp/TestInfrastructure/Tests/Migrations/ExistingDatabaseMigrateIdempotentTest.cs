using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations;

/// <summary>
/// Test 2: Existing database → MigrateAsync() is no-op
/// Verifies that MigrateAsync() is idempotent and safe to call multiple times
/// </summary>
internal class ExistingDatabaseMigrateIdempotentTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Migration_ExistingDatabaseIdempotent";

    // Must manage own lifecycle - needs database created via MigrateAsync, not EnsureCreated
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Create database using MigrateAsync (not EnsureCreated)
        await context.Database.MigrateAsync();

        // Add test data
        var item1 = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Before Migration 1",
            Description = "Test",
            UpdatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(item1);
        await context.SaveChangesAsync();

        var countBefore = await context.TodoItems.CountAsync();

        // Apply migrations (should be no-op since database exists)
        await context.Database.MigrateAsync();

        // Verify data is still there
        var countAfter = await context.TodoItems.CountAsync();
        if (countAfter != countBefore)
        {
            throw new InvalidOperationException($"Data count changed: {countBefore} → {countAfter}");
        }

        // Verify the original item still exists
        var retrieved = await context.TodoItems.FindAsync(item1.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Original item was lost after MigrateAsync");
        }

        if (retrieved.Title != "Before Migration 1")
        {
            throw new InvalidOperationException("Original item data was corrupted");
        }

        // Call MigrateAsync again (should still be idempotent)
        await context.Database.MigrateAsync();

        var countAfterSecond = await context.TodoItems.CountAsync();
        if (countAfterSecond != countBefore)
        {
            throw new InvalidOperationException($"Data count changed after second migrate: {countBefore} → {countAfterSecond}");
        }

        return "OK";
    }
}
