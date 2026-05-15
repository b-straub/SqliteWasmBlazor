using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests the import workflow with transient backup:
/// 1. Create data in existing DB
/// 2. Export it for later import
/// 3. Create different data
/// 4. Import the exported file (simulates the page workflow: backup → import → delete backup)
/// 5. Verify imported data replaced the existing data
/// 6. Verify backup was cleaned up
/// </summary>
internal class RawDatabaseImportWithBackupTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_WithBackup";

    private const string DbName = "TestDb.db";
    private const string BackupName = "TestDb.backup.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Step 1: Create initial data and export it
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Original Item",
                Description = "From exported DB",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var exportedBytes = await DatabaseService.ExportDatabaseAsync(DbName);

        // Re-open after export (export closes the DB)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        // Step 2: Replace data with something different
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
            context.TodoItems.AddRange(
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "New Item A", Description = "Will be replaced",
                    IsCompleted = false, UpdatedAt = DateTime.UtcNow
                },
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "New Item B", Description = "Will be replaced",
                    IsCompleted = true, UpdatedAt = DateTime.UtcNow
                }
            );
            await context.SaveChangesAsync();
        }

        // Step 3: Simulate the page import workflow — backup → import → cleanup
        // Remove old backup if exists
        if (await DatabaseService.ExistsDatabaseAsync(BackupName))
        {
            await DatabaseService.DeleteDatabaseAsync(BackupName);
        }

        // Close and rename current to backup
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.RenameDatabaseAsync(DbName, BackupName);

        // Verify backup exists
        if (!await DatabaseService.ExistsDatabaseAsync(BackupName))
        {
            throw new InvalidOperationException("Backup database was not created");
        }

        // Import the exported data
        await DatabaseService.ImportDatabaseAsync(DbName, exportedBytes);

        // Re-open
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        // Step 4: Verify imported data (should be the original 1 item, not the 2 new items)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 1)
            {
                throw new InvalidOperationException($"Expected 1 item after import, got {count}");
            }

            var item = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Original Item");
            if (item is null)
            {
                throw new InvalidOperationException("Expected 'Original Item' after import but not found");
            }
        }

        // Step 5: Clean up backup (success path)
        await DatabaseService.DeleteDatabaseAsync(BackupName);

        if (await DatabaseService.ExistsDatabaseAsync(BackupName))
        {
            throw new InvalidOperationException("Backup was not cleaned up after successful import");
        }

        return "OK";
    }
}
