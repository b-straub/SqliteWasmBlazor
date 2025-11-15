using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Extensions;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ExportImportLargeDatasetTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_LargeDataset";

    public override async ValueTask<string?> RunTestAsync()
    {
        // Skip in CI - IncrementalBatches test provides sufficient coverage
        var isCI = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
                   !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

        if (isCI)
        {
            return "SKIPPED (CI - use IncrementalBatches for coverage)";
        }

        const int itemCount = 10000;
        const string schemaVersion = "1.0";
        const string appId = "SqliteWasmBlazor.Test";

        // Create large dataset
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = new List<TodoItem>();
            for (var i = 0; i < itemCount; i++)
            {
                items.Add(new TodoItem
                {
                    Title = $"Task {i}",
                    Description = $"Description for task {i}",
                    IsCompleted = i % 2 == 0,
                    CreatedAt = DateTime.UtcNow.AddDays(-i),
                    CompletedAt = i % 2 == 0 ? DateTime.UtcNow.AddDays(-i / 2) : null
                });
            }

            context.TodoItems.AddRange(items);
            await context.SaveChangesAsync();
        }

        // Export to stream in pages (simulate pagination)
        using var exportStream = new MemoryStream();
        const int pageSize = 1000;
        var totalPages = (itemCount + pageSize - 1) / pageSize;

        for (var page = 0; page < totalPages; page++)
        {
            await using var context = await Factory.CreateDbContextAsync();
            var items = await context.TodoItems
                .AsNoTracking()
                .OrderBy(t => t.Id)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var dtos = items.Select(TodoItemDto.FromEntity).ToList();

            // Only write header on first page
            if (page == 0)
            {
                var header = MessagePackFileHeader.Create<TodoItemDto>(itemCount, schemaVersion, appId);
                await MessagePack.MessagePackSerializer.SerializeAsync(exportStream, header);
            }

            // Write items
            foreach (var dto in dtos)
            {
                await MessagePack.MessagePackSerializer.SerializeAsync(exportStream, dto);
            }
        }

        exportStream.Position = 0;

        // Clear database using direct SQL (more reliable than RemoveRange)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
        }

        // Import from stream in batches
        var totalImported = await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            exportStream,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();
                await ImportExportTestHelper.BulkInsertTodoItemsAsync(context, dtos);
            },
            schemaVersion,
            appId,
            batchSize: 500);

        // Verify count
        if (totalImported != itemCount)
        {
            throw new InvalidOperationException($"Expected {itemCount} items imported, got {totalImported}");
        }

        // Verify data in database
        await using (var verifyContext = await Factory.CreateDbContextAsync())
        {
            var count = await verifyContext.TodoItems.CountAsync();
            if (count != itemCount)
            {
                throw new InvalidOperationException($"Expected {itemCount} items in database, got {count}");
            }

            // Sample check: verify first and last item
            var firstItem = await verifyContext.TodoItems.OrderBy(t => t.Id).FirstAsync();
            if (firstItem.Title != "Task 0")
            {
                throw new InvalidOperationException("First item title mismatch");
            }

            var lastItem = await verifyContext.TodoItems.OrderBy(t => t.Id).LastAsync();
            if (lastItem.Title != $"Task {itemCount - 1}")
            {
                throw new InvalidOperationException("Last item title mismatch");
            }
        }

        return "OK";
    }
}
