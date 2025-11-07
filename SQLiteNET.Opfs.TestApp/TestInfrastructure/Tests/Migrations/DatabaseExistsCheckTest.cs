using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Migrations;

/// <summary>
/// Test: Verify Database.CanConnect() and other existence checks work properly
/// </summary>
internal class DatabaseExistsCheckTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Migration_DatabaseExistsCheck";

    // Migration tests manage their own database lifecycle
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Verify database doesn't exist
        var canConnectBefore = await context.Database.CanConnectAsync();
        if (canConnectBefore)
        {
            throw new InvalidOperationException("Database should not be connectable after EnsureDeletedAsync");
        }

        // Create database using MigrateAsync
        await context.Database.MigrateAsync();

        // Verify database now exists
        var canConnectAfter = await context.Database.CanConnectAsync();
        if (!canConnectAfter)
        {
            throw new InvalidOperationException("Database should be connectable after MigrateAsync");
        }

        // Verify we can actually query it
        var canQuery = await CanQueryDatabaseAsync(context);
        if (!canQuery)
        {
            throw new InvalidOperationException("Should be able to query database after MigrateAsync");
        }

        // Test with EnsureCreated instead
        await context.Database.EnsureDeletedAsync();

        var canConnectAfterDelete = await context.Database.CanConnectAsync();
        if (canConnectAfterDelete)
        {
            throw new InvalidOperationException("Database should not be connectable after second delete");
        }

        await context.Database.EnsureCreatedAsync();

        var canConnectAfterCreate = await context.Database.CanConnectAsync();
        if (!canConnectAfterCreate)
        {
            throw new InvalidOperationException("Database should be connectable after EnsureCreatedAsync");
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
}
