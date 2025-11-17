using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests basic patch export/import functionality.
/// Scenario: Export initial data, modify some items, export patches, import to second database.
/// </summary>
internal class ExportImportDeltaBasicTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_DeltaBasic";

    public override async ValueTask<string?> RunTestAsync()
    {
        var schemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>();
        const string appId = "SqliteWasmBlazor.Test";

        // Step 1: Create initial dataset
        var initialItems = new List<TodoItem>
        {
            new() { Id = Guid.NewGuid(), Title = "Original 1", Description = "Description 1", IsCompleted = false, UpdatedAt = DateTime.UtcNow.AddHours(-2) },
            new() { Id = Guid.NewGuid(), Title = "Original 2", Description = "Description 2", IsCompleted = false, UpdatedAt = DateTime.UtcNow.AddHours(-2) },
            new() { Id = Guid.NewGuid(), Title = "Original 3", Description = "Description 3", IsCompleted = false, UpdatedAt = DateTime.UtcNow.AddHours(-2) }
        };

        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.AddRange(initialItems);
            await context.SaveChangesAsync();
        }

        // Step 2: Export initial data
        using var initialExportStream = new MemoryStream();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var items = await context.TodoItems.AsNoTracking().ToListAsync();
            var dtos = items.Select(TodoItemDto.FromEntity).ToList();

            await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
                dtos,
                initialExportStream,
                appId);
        }

        // Remember the cutoff time for patches (before modifications)
        var patchCutoffTime = DateTime.UtcNow.AddMinutes(-1);

        // Step 3: Modify some items and add a new one
        await Task.Delay(100); // Ensure UpdatedAt differs
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item1 = await context.TodoItems.FirstAsync(t => t.Title == "Original 1");
            item1.Title = "Modified 1";
            item1.Description = "Updated description";
            item1.IsCompleted = true;
            item1.UpdatedAt = DateTime.UtcNow;
            item1.CompletedAt = DateTime.UtcNow;

            var item2 = await context.TodoItems.FirstAsync(t => t.Title == "Original 2");
            item2.IsCompleted = true;
            item2.UpdatedAt = DateTime.UtcNow;
            item2.CompletedAt = DateTime.UtcNow;

            // Add new item
            context.TodoItems.Add(new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "New Item",
                Description = "Added after initial export",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            });

            await context.SaveChangesAsync();
        }

        // Step 4: Export patches (only modified/new items)
        using var patchStream = new MemoryStream();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var modifiedItems = await context.TodoItems
                .AsNoTracking()
                .Where(t => t.UpdatedAt > patchCutoffTime)
                .ToListAsync();

            if (modifiedItems.Count != 3)
            {
                throw new InvalidOperationException($"Expected 3 modified items in patch, got {modifiedItems.Count}");
            }

            var dtos = modifiedItems.Select(TodoItemDto.FromEntity).ToList();

            await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
                dtos,
                patchStream,
                appId);
        }

        patchStream.Position = 0;

        // Step 5: Create second database and import initial data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
        }

        initialExportStream.Position = 0;
        await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            initialExportStream,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();
                await ImportExportTestHelper.BulkInsertTodoItemsAsync(context, dtos);
            },
            schemaHash,
            appId);

        // Step 6: Apply patches to second database
        var patchesApplied = await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            patchStream,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();

                foreach (var dto in dtos)
                {
                    var existing = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == dto.Id);
                    if (existing is not null)
                    {
                        // Update existing item
                        existing.Title = dto.Title;
                        existing.Description = dto.Description;
                        existing.IsCompleted = dto.IsCompleted;
                        existing.UpdatedAt = dto.UpdatedAt;
                        existing.CompletedAt = dto.CompletedAt;
                    }
                    else
                    {
                        // Insert new item
                        context.TodoItems.Add(dto.ToEntity());
                    }
                }

                await context.SaveChangesAsync();
            },
            schemaHash,
            appId);

        if (patchesApplied != 3)
        {
            throw new InvalidOperationException($"Expected 3 patches applied, got {patchesApplied}");
        }

        // Step 7: Verify second database has correct data
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var count = await context.TodoItems.CountAsync();
            if (count != 4) // 3 original + 1 new
            {
                throw new InvalidOperationException($"Expected 4 items in database, got {count}");
            }

            var modified1 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Modified 1");
            if (modified1 is null || !modified1.IsCompleted || modified1.Description != "Updated description")
            {
                throw new InvalidOperationException("Modified 1 patch not applied correctly");
            }

            var modified2 = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Original 2");
            if (modified2 is null || !modified2.IsCompleted)
            {
                throw new InvalidOperationException("Original 2 patch not applied correctly");
            }

            var unchanged = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "Original 3");
            if (unchanged is null || unchanged.IsCompleted)
            {
                throw new InvalidOperationException("Original 3 should remain unchanged");
            }

            var newItem = await context.TodoItems.FirstOrDefaultAsync(t => t.Title == "New Item");
            if (newItem is null)
            {
                throw new InvalidOperationException("New item not found in patched database");
            }
        }

        return "OK";
    }
}
