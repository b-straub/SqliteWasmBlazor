using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations;

/// <summary>
/// Test 5: EnsureCreated() vs MigrateAsync() conflict handling
/// Verifies behavior when mixing EnsureCreated and MigrateAsync
/// </summary>
internal class EnsureCreatedVsMigrateConflictTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Migration_EnsureCreatedVsMigrateConflict";

    // Migration tests manage their own database lifecycle
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Scenario 1: EnsureCreated first, then MigrateAsync
        var wasCreated = await context.Database.EnsureCreatedAsync();
        if (!wasCreated)
        {
            throw new InvalidOperationException("EnsureCreatedAsync should have created database");
        }

        // Add test data
        var item1 = new TodoItem
        {
            Title = "After EnsureCreated",
            Description = "Test",
            CreatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(item1);
        await context.SaveChangesAsync();

        // Now call MigrateAsync - this SHOULD fail because tables already exist
        // EnsureCreatedAsync doesn't create __EFMigrationsHistory, so MigrateAsync thinks
        // no migrations have been applied and tries to create tables again
        try
        {
            await context.Database.MigrateAsync();
            throw new InvalidOperationException("MigrateAsync should fail when called after EnsureCreatedAsync");
        }
        catch (Exception ex) when (ex.Message.Contains("already exists"))
        {
            // Expected: Can't migrate a database created with EnsureCreated
            // This is correct behavior - don't mix EnsureCreated and Migrate
        }

        // Scenario 2: Check that EnsureCreated is idempotent
        var wasCreatedAgain = await context.Database.EnsureCreatedAsync();
        if (wasCreatedAgain)
        {
            throw new InvalidOperationException("EnsureCreatedAsync should return false when database exists");
        }

        // Verify data still exists (EnsureCreated should be no-op)
        var item1Retrieved = await context.TodoItems.FindAsync(item1.Id);
        if (item1Retrieved is null)
        {
            throw new InvalidOperationException("Data lost after second EnsureCreated call");
        }

        // Scenario 3: Fresh start with MigrateAsync, then EnsureCreated
        // Use a new context to avoid tracking conflicts
        await using (var context2 = await Factory.CreateDbContextAsync())
        {
            await context2.Database.EnsureDeletedAsync();
            await context2.Database.MigrateAsync();

            var item2 = new TodoItem
            {
                Title = "After Migrate",
                Description = "Test",
                CreatedAt = DateTime.UtcNow
            };

            context2.TodoItems.Add(item2);
            await context2.SaveChangesAsync();

            // Call EnsureCreated (should be no-op)
            var wasCreatedAfterMigrate = await context2.Database.EnsureCreatedAsync();
            if (wasCreatedAfterMigrate)
            {
                throw new InvalidOperationException("EnsureCreatedAsync should return false after MigrateAsync");
            }

            // Verify data preserved
            var retrievedItem2 = await context2.TodoItems.FindAsync(item2.Id);
            if (retrievedItem2 is null)
            {
                throw new InvalidOperationException("Data lost after EnsureCreated following MigrateAsync");
            }
        }

        return "OK";
    }
}
