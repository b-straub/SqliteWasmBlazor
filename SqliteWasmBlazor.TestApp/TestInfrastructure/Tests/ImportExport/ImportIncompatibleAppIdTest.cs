using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class ImportIncompatibleAppIdTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ImportIncompatibleAppId";

    public override async ValueTask<string?> RunTestAsync()
    {
        var schemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>();
        const string exportAppId = "AnotherApp";
        const string importAppId = "SqliteWasmBlazor.Test";

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

        // Export with different app ID
        using var exportStream = new MemoryStream();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = await context.TodoItems.AsNoTracking().ToListAsync();
            var dtos = items.Select(TodoItemDto.FromEntity).ToList();

            await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
                dtos,
                exportStream,
                exportAppId);
        }

        exportStream.Position = 0;

        // Try to import expecting different app ID - should fail
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
                schemaHash,
                importAppId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Incompatible application"))
        {
            exceptionThrown = true;
        }

        if (!exceptionThrown)
        {
            throw new InvalidOperationException("Expected app identifier mismatch exception was not thrown");
        }

        return "OK";
    }
}
