using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests importing a raw DB file when no database exists yet (no backup needed).
/// This is the path taken when the DB doesn't exist in OPFS.
/// </summary>
internal class RawDatabaseImportIntoNewTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_IntoNewDatabase";

    private const string DbName = "TestDb.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Step 1: Create data and export
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.AddRange(
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "Import Target 1", Description = "For new DB",
                    IsCompleted = false, UpdatedAt = DateTime.UtcNow
                },
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "Import Target 2", Description = "For new DB",
                    IsCompleted = true, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
                }
            );
            await context.SaveChangesAsync();
        }

        var exportedBytes = await DatabaseService.ExportDatabaseAsync(DbName);

        // Step 2: Delete the database entirely
        await DatabaseService.DeleteDatabaseAsync(DbName);

        // Verify it's gone
        if (await DatabaseService.ExistsDatabaseAsync(DbName))
        {
            throw new InvalidOperationException("Database still exists after delete");
        }

        // Step 3: Import into non-existent DB (no backup path)
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, exportedBytes);

        // Step 4: Re-open and verify
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 2)
            {
                throw new InvalidOperationException($"Expected 2 items after import into new DB, got {count}");
            }

            var item1 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Import Target 1");
            if (item1 is null || item1.IsCompleted)
            {
                throw new InvalidOperationException("Import Target 1 data mismatch");
            }

            var item2 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Import Target 2");
            if (item2 is null || !item2.IsCompleted || item2.CompletedAt is null)
            {
                throw new InvalidOperationException("Import Target 2 data mismatch");
            }
        }

        return "OK";
    }
}
