using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ExportImportEmptyDatabaseTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_EmptyDatabase";

    public override async ValueTask<string?> RunTestAsync()
    {
        const string schemaVersion = "1.0";
        const string appId = "SqliteWasmBlazor.Test";

        // Export empty database
        using var exportStream = new MemoryStream();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = await context.TodoItems.AsNoTracking().ToListAsync();

            if (items.Count != 0)
            {
                throw new InvalidOperationException($"Expected empty database, found {items.Count} items");
            }

            var dtos = items.Select(TodoItemDto.FromEntity).ToList();

            await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
                dtos,
                exportStream,
                schemaVersion,
                appId);
        }

        // Verify export stream has only header (non-zero but minimal)
        if (exportStream.Length == 0)
        {
            throw new InvalidOperationException("Export stream should contain header even for empty database");
        }

        exportStream.Position = 0;

        // Import from empty export
        var totalImported = await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            exportStream,
            async dtos =>
            {
                if (dtos.Count > 0)
                {
                    throw new InvalidOperationException($"Expected empty batch, got {dtos.Count} items");
                }
                await Task.CompletedTask;
            },
            schemaVersion,
            appId);

        // Verify no items were imported
        if (totalImported != 0)
        {
            throw new InvalidOperationException($"Expected 0 items imported, got {totalImported}");
        }

        // Verify database is still empty
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 0)
            {
                throw new InvalidOperationException($"Expected 0 items in database, got {count}");
            }
        }

        return "OK";
    }
}
