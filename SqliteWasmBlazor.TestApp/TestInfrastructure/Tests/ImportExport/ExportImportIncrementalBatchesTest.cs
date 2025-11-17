using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ExportImportIncrementalBatchesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_IncrementalBatches";

    public override async ValueTask<string?> RunTestAsync()
    {
        const int itemCount = 2500;
        const int batchSize = 500;
        var schemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>();
        const string appId = "SqliteWasmBlazor.Test";

        // Create test data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = new List<TodoItem>();
            for (var i = 0; i < itemCount; i++)
            {
                items.Add(new TodoItem
                {
                    Id = Guid.NewGuid(),
                    Title = $"Task {i}",
                    Description = $"Description {i}",
                    IsCompleted = i % 3 == 0,
                    UpdatedAt = DateTime.UtcNow.AddHours(-i),
                    CompletedAt = i % 3 == 0 ? DateTime.UtcNow.AddHours(-i / 2) : null
                });
            }

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

        exportStream.Position = 0;

        // Clear database using direct SQL (more reliable than RemoveRange)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
        }

        // Import with batch size tracking
        var batchCount = 0;
        var itemsPerBatch = new List<int>();

        var totalImported = await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            exportStream,
            async dtos =>
            {
                batchCount++;
                itemsPerBatch.Add(dtos.Count);

                await using var context = await Factory.CreateDbContextAsync();
                await ImportExportTestHelper.BulkInsertTodoItemsAsync(context, dtos);
            },
            schemaHash,
            appId,
            batchSize: batchSize);

        // Verify total imported
        if (totalImported != itemCount)
        {
            throw new InvalidOperationException($"Expected {itemCount} items imported, got {totalImported}");
        }

        // Verify batch count
        var expectedBatches = (itemCount + batchSize - 1) / batchSize;
        if (batchCount != expectedBatches)
        {
            throw new InvalidOperationException($"Expected {expectedBatches} batches, got {batchCount}");
        }

        // Verify batch sizes
        for (var i = 0; i < batchCount - 1; i++)
        {
            if (itemsPerBatch[i] != batchSize)
            {
                throw new InvalidOperationException($"Expected batch {i} to have {batchSize} items, got {itemsPerBatch[i]}");
            }
        }

        // Last batch may be smaller
        var expectedLastBatchSize = itemCount % batchSize == 0 ? batchSize : itemCount % batchSize;
        if (itemsPerBatch[^1] != expectedLastBatchSize)
        {
            throw new InvalidOperationException($"Expected last batch to have {expectedLastBatchSize} items, got {itemsPerBatch[^1]}");
        }

        // Verify total in database
        await using (var verifyContext = await Factory.CreateDbContextAsync())
        {
            var count = await verifyContext.TodoItems.CountAsync();
            if (count != itemCount)
            {
                throw new InvalidOperationException($"Expected {itemCount} items in database, got {count}");
            }
        }

        return "OK";
    }
}
