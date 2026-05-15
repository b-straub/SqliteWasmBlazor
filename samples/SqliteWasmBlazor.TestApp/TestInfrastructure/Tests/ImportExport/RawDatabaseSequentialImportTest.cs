using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests that multiple sequential imports work correctly.
/// This was a real bug: the second import failed with "Database not open"
/// because the C# connection state was stale from the first import.
/// </summary>
internal class RawDatabaseSequentialImportTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_SequentialImports";

    private const string DbName = "TestDb.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Create two different database snapshots
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = Guid.NewGuid(), Title = "Snapshot A", Description = "First snapshot",
                IsCompleted = false, UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var snapshotA = await DatabaseService.ExportDatabaseAsync(DbName);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = Guid.NewGuid(), Title = "Snapshot B", Description = "Second snapshot",
                IsCompleted = true, UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var snapshotB = await DatabaseService.ExportDatabaseAsync(DbName);

        // First import: restore snapshot A (1 item)
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, snapshotA);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 1)
            {
                throw new InvalidOperationException($"After first import: expected 1 item, got {count}");
            }
        }

        // Second import: restore snapshot B (2 items) — this was the failing case
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, snapshotB);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 2)
            {
                throw new InvalidOperationException($"After second import: expected 2 items, got {count}");
            }
        }

        // Third import: back to snapshot A — verify it keeps working
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, snapshotA);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 1)
            {
                throw new InvalidOperationException($"After third import: expected 1 item, got {count}");
            }

            var item = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Snapshot A");
            if (item is null)
            {
                throw new InvalidOperationException("Snapshot A data not found after third import");
            }
        }

        return "OK";
    }
}
