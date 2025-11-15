using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Extensions;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ExportImportRoundTripTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_RoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        const string schemaVersion = "1.0";
        const string appId = "SqliteWasmBlazor.Test";

        // Create test data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = new List<TodoItem>
            {
                new() { Title = "Task 1", Description = "Description 1", IsCompleted = false, CreatedAt = DateTime.UtcNow },
                new() { Title = "Task 2", Description = "Description 2", IsCompleted = true, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow },
                new() { Title = "Task 3", Description = string.Empty, IsCompleted = false, CreatedAt = DateTime.UtcNow }
            };

            context.TodoItems.AddRange(items);
            await context.SaveChangesAsync();
        }

        // Export to stream
        using var exportStream = new MemoryStream();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = await context.TodoItems.AsNoTracking().ToListAsync();
            var dtos = items.Select(TodoItemDto.FromEntity).ToList();

            await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
                dtos,
                exportStream,
                schemaVersion,
                appId);
        }

        // Verify export stream has data
        if (exportStream.Length == 0)
        {
            throw new InvalidOperationException("Export stream is empty");
        }

        exportStream.Position = 0;

        // Clear database using direct SQL (more reliable than RemoveRange)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
        }

        // Verify database is empty
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 0)
            {
                throw new InvalidOperationException($"Database should be empty but has {count} items");
            }
        }

        // Import from stream
        var importedCount = 0;
        var totalImported = await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            exportStream,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();
                await ImportExportTestHelper.BulkInsertTodoItemsAsync(context, dtos);
                importedCount += dtos.Count;
            },
            schemaVersion,
            appId);

        // Verify import
        if (totalImported != 3)
        {
            throw new InvalidOperationException($"Expected 3 items imported, got {totalImported}");
        }

        if (importedCount != 3)
        {
            throw new InvalidOperationException($"Expected 3 items in batch, got {importedCount}");
        }

        // Verify data matches
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = await context.TodoItems.OrderBy(t => t.Id).ToListAsync();

            if (items.Count != 3)
            {
                throw new InvalidOperationException($"Expected 3 items in database, got {items.Count}");
            }

            if (items[0].Title != "Task 1" || items[0].IsCompleted)
            {
                throw new InvalidOperationException("Task 1 data mismatch");
            }

            if (items[1].Title != "Task 2" || !items[1].IsCompleted || items[1].CompletedAt is null)
            {
                throw new InvalidOperationException("Task 2 data mismatch");
            }

            if (items[2].Title != "Task 3" || items[2].Description != string.Empty)
            {
                throw new InvalidOperationException("Task 3 data mismatch");
            }
        }

        return "OK";
    }
}
