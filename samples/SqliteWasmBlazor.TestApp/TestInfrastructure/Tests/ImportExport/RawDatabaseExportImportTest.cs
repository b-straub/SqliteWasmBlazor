using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class RawDatabaseExportImportTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ExportImport_RawDatabase";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Create 3 TodoItems via EF Core
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.AddRange(
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "Raw Task 1", Description = "Desc 1",
                    IsCompleted = false, UpdatedAt = DateTime.UtcNow
                },
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "Raw Task 2", Description = "Desc 2",
                    IsCompleted = true, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
                },
                new TodoItem
                {
                    Id = Guid.NewGuid(), Title = "Raw Task 3", Description = "Desc 3",
                    IsCompleted = false, UpdatedAt = DateTime.UtcNow
                }
            );
            await context.SaveChangesAsync();
        }

        // Close EF connection so worker can access db file exclusively
        // (CreateDbContextAsync opens a new connection each time, so just dispose)

        // Export raw db
        var exportedBytes = await DatabaseService.ExportDatabaseAsync("TestDb.db");

        // Verify exported bytes start with SQLite header ("SQLite format 3\0")
        if (exportedBytes.Length < 16)
        {
            throw new InvalidOperationException($"Exported data too small: {exportedBytes.Length} bytes");
        }

        var header = System.Text.Encoding.ASCII.GetString(exportedBytes, 0, 15);
        if (header != "SQLite format 3")
        {
            throw new InvalidOperationException($"Invalid SQLite header: '{header}'");
        }

        // Delete database
        await DatabaseService.DeleteDatabaseAsync("TestDb.db");

        // Import raw db
        await DatabaseService.ImportDatabaseAsync("TestDb.db", exportedBytes);

        // Re-open via new DbContext, verify 3 items with correct data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 3)
            {
                throw new InvalidOperationException($"Expected 3 items after import, got {count}");
            }

            var task1 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Raw Task 1");
            if (task1 is null || task1.IsCompleted)
            {
                throw new InvalidOperationException("Raw Task 1 data mismatch after import");
            }

            var task2 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Raw Task 2");
            if (task2 is null || !task2.IsCompleted || task2.CompletedAt is null)
            {
                throw new InvalidOperationException("Raw Task 2 data mismatch after import");
            }

            var task3 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Raw Task 3");
            if (task3 is null || task3.Description != "Desc 3")
            {
                throw new InvalidOperationException("Raw Task 3 data mismatch after import");
            }
        }

        return "OK";
    }
}
