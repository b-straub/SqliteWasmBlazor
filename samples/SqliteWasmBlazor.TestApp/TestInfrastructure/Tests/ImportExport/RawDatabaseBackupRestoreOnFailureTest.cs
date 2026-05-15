using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests backup restore on import failure:
/// 1. Create data in existing DB
/// 2. Attempt import with invalid data (should fail)
/// 3. Restore from backup
/// 4. Verify original data is intact
/// </summary>
internal class RawDatabaseBackupRestoreOnFailureTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_BackupRestoreOnFailure";

    private const string DbName = "TestDb.db";
    private const string BackupName = "TestDb.backup.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Step 1: Create data
        var originalId = Guid.NewGuid();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.AddRange(
                new TodoItem
                {
                    Id = originalId, Title = "Preserved Item", Description = "Must survive failed import",
                    IsCompleted = false, UpdatedAt = DateTime.UtcNow
                },
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "Also Preserved", Description = "Must survive",
                    IsCompleted = true, UpdatedAt = DateTime.UtcNow
                }
            );
            await context.SaveChangesAsync();
        }

        // Step 2: Create backup (simulate page workflow)
        if (await DatabaseService.ExistsDatabaseAsync(BackupName))
        {
            await DatabaseService.DeleteDatabaseAsync(BackupName);
        }

        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.RenameDatabaseAsync(DbName, BackupName);

        // Step 3: Attempt import with invalid data. ImportDatabaseAsync
        // auto-detects by the "SQLite format 3" magic and accepts opaque
        // bytes (required for encrypted-DB backup restore) — so the import
        // itself succeeds. The error surfaces on the next open attempt, at
        // which point an application is expected to fall back to the backup.
        var invalidData = new byte[1024];
        Random.Shared.NextBytes(invalidData);

        await DatabaseService.ImportDatabaseAsync(DbName, invalidData);

        try
        {
            await using var probe = await Factory.CreateDbContextAsync();
            await probe.TodoItems.CountAsync();
            throw new InvalidOperationException(
                "Expected open of corrupted DB to fail, but query succeeded");
        }
        catch (Exception ex) when (ex is not InvalidOperationException
            || !ex.Message.Contains("Expected open of corrupted DB"))
        {
            // Expected — SQLite rejects the random-bytes file as not-a-database.
            // Production code path would now trigger the restore below.
        }

        // Step 4: Restore from backup (simulate page failure recovery)
        if (await DatabaseService.ExistsDatabaseAsync(DbName))
        {
            await DatabaseService.DeleteDatabaseAsync(DbName);
        }

        await DatabaseService.RenameDatabaseAsync(BackupName, DbName);

        // Step 5: Re-open and verify original data is intact
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 2)
            {
                throw new InvalidOperationException($"Expected 2 items after restore, got {count}");
            }

            var preserved = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == originalId);
            if (preserved is null || preserved.Title != "Preserved Item")
            {
                throw new InvalidOperationException("Original data not restored correctly");
            }
        }

        // Verify backup is gone after restore
        if (await DatabaseService.ExistsDatabaseAsync(BackupName))
        {
            throw new InvalidOperationException("Backup should not exist after restore");
        }

        return "OK";
    }
}
