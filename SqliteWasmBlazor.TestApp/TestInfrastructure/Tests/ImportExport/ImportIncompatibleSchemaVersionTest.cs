using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ImportIncompatibleSchemaVersionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ImportIncompatibleSchemaHash";

    public override async ValueTask<string?> RunTestAsync()
    {
        const string appId = "SqliteWasmBlazor.Test";

        // Create test data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Test Task",
                Description = "Test Description",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            };

            context.TodoItems.Add(item);
            await context.SaveChangesAsync();
        }

        // Export with automatic schema hash computation
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

        // Try to import with incorrect schema hash - should fail
        var exceptionThrown = false;
        var fakeSchemaHash = "0000000000000000"; // Fake hash that won't match
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
                fakeSchemaHash,
                appId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Incompatible schema"))
        {
            exceptionThrown = true;
        }

        if (!exceptionThrown)
        {
            throw new InvalidOperationException("Expected schema hash mismatch exception was not thrown");
        }

        return "OK";
    }
}
