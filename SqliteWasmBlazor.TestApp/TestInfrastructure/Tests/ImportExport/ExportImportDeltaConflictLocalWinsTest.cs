using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;
using SqliteWasmBlazor.Models.Enums;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests Local-Wins conflict resolution strategy.
/// Scenario: Local changes always win, imported items only added if they don't exist.
/// </summary>
internal class ExportImportDeltaConflictLocalWinsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_DeltaConflict_LocalWins";

    public override async ValueTask<string?> RunTestAsync()
    {
        var schemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>();
        const string appId = "SqliteWasmBlazor.Test";

        // Step 1: Create local item
        var sharedId = Guid.NewGuid();
        var localTime = DateTime.UtcNow;

        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = sharedId,
                Title = "Local Item",
                Description = "Local version",
                IsCompleted = false,
                UpdatedAt = localTime
            });
            await context.SaveChangesAsync();
        }

        // Step 2: Create imported item with different data (should be ignored)
        var importTime = localTime.AddMinutes(10); // Newer timestamp, but should still lose
        var importedItem = new TodoItemDto
        {
            Id = sharedId,
            Title = "Imported Item",
            Description = "Imported version (should be ignored)",
            IsCompleted = true,
            UpdatedAt = importTime,
            CompletedAt = importTime
        };

        // Step 3: Create new item that doesn't exist locally (should be added)
        var newItemId = Guid.NewGuid();
        var newItem = new TodoItemDto
        {
            Id = newItemId,
            Title = "New Item",
            Description = "Should be added",
            IsCompleted = false,
            UpdatedAt = importTime
        };

        using var stream = new MemoryStream();
        await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
            new List<TodoItemDto> { importedItem, newItem },
            stream,
            appId);

        stream.Position = 0;

        // Step 4: Import with LocalWins strategy
        var strategy = ConflictResolutionStrategy.LocalWins;
        await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            stream,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();

                foreach (var dto in dtos)
                {
                    var existing = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == dto.Id);

                    if (existing is not null)
                    {
                        // LocalWins: Never update existing items
                        var shouldUpdate = strategy switch
                        {
                            ConflictResolutionStrategy.LocalWins => false,
                            _ => throw new InvalidOperationException($"Wrong strategy: {strategy}")
                        };

                        if (shouldUpdate)
                        {
                            existing.Title = dto.Title;
                            existing.Description = dto.Description;
                            existing.IsCompleted = dto.IsCompleted;
                            existing.UpdatedAt = dto.UpdatedAt;
                            existing.CompletedAt = dto.CompletedAt;
                        }
                    }
                    else
                    {
                        // New items are always added
                        context.TodoItems.Add(dto.ToEntity());
                    }
                }

                await context.SaveChangesAsync();
            },
            schemaHash,
            appId);

        // Step 5: Verify local item was NOT updated
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == sharedId);

            if (item is null)
            {
                throw new InvalidOperationException("Local item not found");
            }

            if (item.Description != "Local version")
            {
                throw new InvalidOperationException($"Local version should be preserved, got: {item.Description}");
            }

            if (item.IsCompleted)
            {
                throw new InvalidOperationException("Local completion status should be preserved (false)");
            }

            if (item.UpdatedAt != localTime)
            {
                throw new InvalidOperationException("Local UpdatedAt should be preserved");
            }
        }

        // Step 6: Verify new item WAS added
        await using (var context2 = await Factory.CreateDbContextAsync())
        {
            var newItemResult = await context2.TodoItems.FirstOrDefaultAsync(t => t.Id == newItemId);

            if (newItemResult is null)
            {
                throw new InvalidOperationException("New item should have been added");
            }

            if (newItemResult.Description != "Should be added")
            {
                throw new InvalidOperationException($"New item description incorrect: {newItemResult.Description}");
            }
        }

        return "OK";
    }
}
