using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Migrations;

/// <summary>
/// Test 1: Fresh database → MigrateAsync() creates schema
/// Verifies that MigrateAsync() can create a database from scratch
/// </summary>
internal class FreshDatabaseMigrateTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Migration_FreshDatabaseMigrate";

    // Migration tests manage their own database lifecycle
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Verify database doesn't exist by checking if we can query
        var canQueryBefore = await CanQueryDatabaseAsync(context);
        if (canQueryBefore)
        {
            throw new InvalidOperationException("Database should not exist after EnsureDeletedAsync");
        }

        // Apply migrations (this should create the schema)
        await context.Database.MigrateAsync();

        // Verify database now exists and has schema
        var canQueryAfter = await CanQueryDatabaseAsync(context);
        if (!canQueryAfter)
        {
            throw new InvalidOperationException("Database should exist after MigrateAsync");
        }

        // Verify migrations history table exists
        var hasMigrationsTable = await HasMigrationsHistoryTableAsync(context);
        if (!hasMigrationsTable)
        {
            throw new InvalidOperationException("__EFMigrationsHistory table should exist after MigrateAsync");
        }

        // Verify we can insert and query data
        var testItem = new TodoItem
        {
            Title = "Migration Test",
            Description = "Testing migrations",
            CreatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(testItem);
        await context.SaveChangesAsync();

        var retrieved = await context.TodoItems.FindAsync(testItem.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve item after migration");
        }

        return "OK";
    }

    private static async Task<bool> CanQueryDatabaseAsync(TodoDbContext context)
    {
        try
        {
            await context.TodoItems.CountAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasMigrationsHistoryTableAsync(TodoDbContext context)
    {
        try
        {
            // Try to query the migrations history table directly
            await context.Database.ExecuteSqlRawAsync("SELECT COUNT(*) FROM \"__EFMigrationsHistory\"");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
