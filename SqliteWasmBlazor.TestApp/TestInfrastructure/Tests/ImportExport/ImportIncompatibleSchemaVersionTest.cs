using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Extensions;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ImportIncompatibleSchemaVersionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ImportIncompatibleSchemaVersion";

    public override async ValueTask<string?> RunTestAsync()
    {
        const string exportSchemaVersion = "2.0";
        const string importSchemaVersion = "1.0";
        const string appId = "SqliteWasmBlazor.Test";

        // Create test data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = new TodoItem
            {
                Title = "Test Task",
                Description = "Test Description",
                IsCompleted = false,
                CreatedAt = DateTime.UtcNow
            };

            context.TodoItems.Add(item);
            await context.SaveChangesAsync();
        }

        // Export with version 2.0
        using var exportStream = new MemoryStream();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = await context.TodoItems.AsNoTracking().ToListAsync();
            var dtos = items.Select(TodoItemDto.FromEntity).ToList();

            await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
                dtos,
                exportStream,
                exportSchemaVersion,
                appId);
        }

        exportStream.Position = 0;

        // Try to import expecting version 1.0 - should fail
        var exceptionThrown = false;
        try
        {
            await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
                exportStream,
                async dtos =>
                {
                    await using var context = await Factory.CreateDbContextAsync();
                    var entities = dtos.Select(dto => dto.ToEntity()).ToList();
                    context.TodoItems.AddRange(entities);
                    await context.SaveChangesAsync();
                },
                importSchemaVersion,
                appId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Incompatible schema version"))
        {
            exceptionThrown = true;
        }

        if (!exceptionThrown)
        {
            throw new InvalidOperationException("Expected schema version mismatch exception was not thrown");
        }

        return "OK";
    }
}
