using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;
using SqliteWasmBlazor.Models.Enums;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests Delta-Wins conflict resolution strategy.
/// Scenario: Imported changes always win, local items are overwritten regardless of timestamp.
/// </summary>
internal class ExportImportDeltaConflictDeltaWinsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_DeltaConflict_DeltaWins";

    public override async ValueTask<string?> RunTestAsync()
    {
        var schemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>();
        const string appId = "SqliteWasmBlazor.Test";

        // Step 1: Create local item with newer timestamp
        var sharedId = Guid.NewGuid();
        var newerLocalTime = DateTime.UtcNow;

        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = sharedId,
                Title = "Local Item",
                Description = "Newer local version",
                IsCompleted = true,
                UpdatedAt = newerLocalTime,
                CompletedAt = newerLocalTime
            });
            await context.SaveChangesAsync();
        }

        // Step 2: Create imported item with OLDER timestamp (should still win with DeltaWins)
        var olderImportTime = newerLocalTime.AddMinutes(-10);
        var importedItem = new TodoItemDto
        {
            Id = sharedId,
            Title = "Imported Item",
            Description = "Older imported version (should win anyway)",
            IsCompleted = false,
            UpdatedAt = olderImportTime,
            CompletedAt = null
        };

        using var stream = new MemoryStream();
        await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
            new List<TodoItemDto> { importedItem },
            stream,
            appId);

        stream.Position = 0;

        // Step 3: Import with DeltaWins strategy
        var strategy = ConflictResolutionStrategy.DeltaWins;
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
                        // DeltaWins: Always update existing items
                        var shouldUpdate = strategy switch
                        {
                            ConflictResolutionStrategy.DeltaWins => true,
                            _ => throw new InvalidOperationException($"Wrong strategy: {strategy}")
                        };

                        if (shouldUpdate)
                        {
                            existing.Title = dto.Title;
                            existing.Description = dto.Description;
                            existing.IsCompleted = dto.IsCompleted;
                            existing.UpdatedAt = dto.UpdatedAt;
                            existing.CompletedAt = dto.CompletedAt;
                            existing.IsDeleted = dto.IsDeleted;
                            existing.DeletedAt = dto.DeletedAt;
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

        // Step 4: Verify imported item WAS applied (even though it's older)
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == sharedId);

            if (item is null)
            {
                throw new InvalidOperationException("Item not found");
            }

            if (item.Description != "Older imported version (should win anyway)")
            {
                throw new InvalidOperationException($"Imported version should win, got: {item.Description}");
            }

            if (item.IsCompleted)
            {
                throw new InvalidOperationException("Imported completion status should be applied (false)");
            }

            if (item.UpdatedAt != olderImportTime)
            {
                throw new InvalidOperationException("Imported UpdatedAt should be applied even though it's older");
            }

            if (item.CompletedAt is not null)
            {
                throw new InvalidOperationException("CompletedAt should be null from import");
            }
        }

        // Step 5: Test with multiple updates to ensure it always applies
        var secondImportTime = olderImportTime.AddMinutes(-5); // Even older!
        var secondImport = new TodoItemDto
        {
            Id = sharedId,
            Title = "Second Import",
            Description = "Even older import (should still win)",
            IsCompleted = true,
            UpdatedAt = secondImportTime,
            CompletedAt = secondImportTime
        };

        using var stream2 = new MemoryStream();
        await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
            new List<TodoItemDto> { secondImport },
            stream2,
            appId);

        stream2.Position = 0;

        await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            stream2,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();

                foreach (var dto in dtos)
                {
                    var existing = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == dto.Id);
                    if (existing is not null)
                    {
                        // DeltaWins: always update
                        existing.Title = dto.Title;
                        existing.Description = dto.Description;
                        existing.IsCompleted = dto.IsCompleted;
                        existing.UpdatedAt = dto.UpdatedAt;
                        existing.CompletedAt = dto.CompletedAt;
                    }
                }

                await context.SaveChangesAsync();
            },
            schemaHash,
            appId);

        // Verify second import also applied
        await using (var finalContext = await Factory.CreateDbContextAsync())
        {
            var item = await finalContext.TodoItems.FirstOrDefaultAsync(t => t.Id == sharedId);

            if (item is null)
            {
                throw new InvalidOperationException("Item not found after second import");
            }

            if (item.Description != "Even older import (should still win)")
            {
                throw new InvalidOperationException($"Second import should win, got: {item.Description}");
            }

            if (!item.IsCompleted)
            {
                throw new InvalidOperationException("Second import completion should be applied (true)");
            }
        }

        return "OK";
    }
}
