using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ExportImportRoundTripTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_RoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        const string appId = "SqliteWasmBlazor.Test";
        var schemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>();

        // Create test data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = new List<TodoItem>
            {
                new() { Id = Guid.NewGuid(), Title = "Task 1", Description = "Description 1", IsCompleted = false, UpdatedAt = DateTime.UtcNow },
                new() { Id = Guid.NewGuid(), Title = "Task 2", Description = "Description 2", IsCompleted = true, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow },
                new() { Id = Guid.NewGuid(), Title = "Task 3", Description = string.Empty, IsCompleted = false, UpdatedAt = DateTime.UtcNow }
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
            schemaHash,
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

        // Verify data matches (search by title, don't rely on Guid ordering)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 3)
            {
                throw new InvalidOperationException($"Expected 3 items in database, got {count}");
            }

            var task1 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Task 1");
            if (task1 is null || task1.IsCompleted)
            {
                throw new InvalidOperationException("Task 1 data mismatch");
            }

            var task2 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Task 2");
            if (task2 is null || !task2.IsCompleted || task2.CompletedAt is null)
            {
                throw new InvalidOperationException("Task 2 data mismatch");
            }

            var task3 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Task 3");
            if (task3 is null || task3.Description != string.Empty)
            {
                throw new InvalidOperationException("Task 3 data mismatch");
            }
        }

        return "OK";
    }
}
