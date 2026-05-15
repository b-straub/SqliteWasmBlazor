using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests that the connection state sync (IsDatabaseOpen) works correctly:
/// after import closes the DB in the worker, the next EF Core query auto-reopens
/// without any manual intervention. This verifies the State property fix.
/// </summary>
internal class RawDatabaseAutoReOpenAfterImportTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_AutoReOpenAfterImport";

    private const string DbName = "TestDb.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Create data and export
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = Guid.NewGuid(), Title = "Auto ReOpen Test", Description = "Test data",
                IsCompleted = false, UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var exportedBytes = await DatabaseService.ExportDatabaseAsync(DbName);

        // Import — worker closes DB during this operation
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, exportedBytes);

        // Directly query via EF Core without any manual open/close dance.
        // This tests that SqliteWasmConnection.State returns Closed (via IsDatabaseOpen check)
        // which forces EF Core to call OpenAsync, which re-opens the DB in the worker.
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 1)
            {
                throw new InvalidOperationException($"Expected 1 item after auto re-open, got {count}");
            }
        }

        // Verify writes work too
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = Guid.NewGuid(), Title = "After Auto ReOpen", Description = "Written after import",
                IsCompleted = false, UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 2)
            {
                throw new InvalidOperationException($"Expected 2 items after write, got {count}");
            }
        }

        return "OK";
    }
}
