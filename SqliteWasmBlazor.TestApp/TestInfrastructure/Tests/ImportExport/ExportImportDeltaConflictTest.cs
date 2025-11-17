using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests patch conflict resolution using last-write-wins strategy based on UpdatedAt.
/// Scenario: Same item modified on both databases, patch import should preserve newer version.
/// </summary>
internal class ExportImportDeltaConflictTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "ExportImport_DeltaConflict";

    public override async ValueTask<string?> RunTestAsync()
    {
        var schemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>();
        const string appId = "SqliteWasmBlazor.Test";

        // Step 1: Create shared item with known Guid
        var sharedId = Guid.NewGuid();
        var baseTime = DateTime.UtcNow.AddHours(-1);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = sharedId,
                Title = "Shared Item",
                Description = "Original version",
                IsCompleted = false,
                UpdatedAt = baseTime
            });
            await context.SaveChangesAsync();
        }

        // Step 2: Simulate local modification (older timestamp)
        var localUpdateTime = baseTime.AddMinutes(5);
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = await context.TodoItems.FirstAsync(t => t.Id == sharedId);
            item.Description = "Local modification";
            item.UpdatedAt = localUpdateTime;
            await context.SaveChangesAsync();
        }

        // Step 3: Create remote modification patch (newer timestamp - should win)
        var remoteUpdateTime = baseTime.AddMinutes(10);
        var remotePatch = new TodoItemDto
        {
            Id = sharedId,
            Title = "Shared Item",
            Description = "Remote modification (newer)",
            IsCompleted = true,
            UpdatedAt = remoteUpdateTime,
            CompletedAt = remoteUpdateTime
        };

        using var patchStream = new MemoryStream();
        await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
            new List<TodoItemDto> { remotePatch },
            patchStream,
            appId);

        patchStream.Position = 0;

        // Step 4: Apply patch with conflict resolution (last-write-wins)
        var conflictsDetected = 0;
        await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            patchStream,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();

                foreach (var dto in dtos)
                {
                    var existing = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == dto.Id);
                    if (existing is not null)
                    {
                        // Detect conflict: both local and remote modified
                        if (existing.UpdatedAt > baseTime)
                        {
                            conflictsDetected++;

                            // Last-write-wins: only apply if remote is newer
                            if (dto.UpdatedAt > existing.UpdatedAt)
                            {
                                existing.Title = dto.Title;
                                existing.Description = dto.Description;
                                existing.IsCompleted = dto.IsCompleted;
                                existing.UpdatedAt = dto.UpdatedAt;
                                existing.CompletedAt = dto.CompletedAt;
                            }
                            // else: local version is newer, keep it
                        }
                        else
                        {
                            // No local modification, safe to apply patch
                            existing.Title = dto.Title;
                            existing.Description = dto.Description;
                            existing.IsCompleted = dto.IsCompleted;
                            existing.UpdatedAt = dto.UpdatedAt;
                            existing.CompletedAt = dto.CompletedAt;
                        }
                    }
                    else
                    {
                        // New item, insert
                        context.TodoItems.Add(dto.ToEntity());
                    }
                }

                await context.SaveChangesAsync();
            },
            schemaHash,
            appId);

        // Step 5: Verify conflict was detected and resolved correctly
        if (conflictsDetected != 1)
        {
            throw new InvalidOperationException($"Expected 1 conflict detected, got {conflictsDetected}");
        }

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == sharedId);

            if (item is null)
            {
                throw new InvalidOperationException("Shared item not found after patch");
            }

            // Remote version should win (newer UpdatedAt)
            if (item.Description != "Remote modification (newer)")
            {
                throw new InvalidOperationException($"Expected remote version to win, got: {item.Description}");
            }

            if (!item.IsCompleted)
            {
                throw new InvalidOperationException("Remote completion status not applied");
            }

            if (item.UpdatedAt != remoteUpdateTime)
            {
                throw new InvalidOperationException("UpdatedAt timestamp not from remote version");
            }
        }

        // Step 6: Test reverse scenario - local newer than remote (local should win)
        var newerLocalTime = remoteUpdateTime.AddMinutes(5);
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = await context.TodoItems.FirstAsync(t => t.Id == sharedId);
            item.Description = "Newer local modification";
            item.UpdatedAt = newerLocalTime;
            await context.SaveChangesAsync();
        }

        // Create older remote patch (should be rejected)
        var olderRemotePatch = new TodoItemDto
        {
            Id = sharedId,
            Title = "Shared Item",
            Description = "Older remote modification",
            IsCompleted = false,
            UpdatedAt = remoteUpdateTime.AddMinutes(2), // Older than newerLocalTime
            CompletedAt = null
        };

        using var olderPatchStream = new MemoryStream();
        await MessagePackSerializer<TodoItemDto>.SerializeStreamAsync(
            new List<TodoItemDto> { olderRemotePatch },
            olderPatchStream,
            appId);

        olderPatchStream.Position = 0;

        await MessagePackSerializer<TodoItemDto>.DeserializeStreamAsync(
            olderPatchStream,
            async dtos =>
            {
                await using var context = await Factory.CreateDbContextAsync();

                foreach (var dto in dtos)
                {
                    var existing = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == dto.Id);
                    if (existing is not null && dto.UpdatedAt > existing.UpdatedAt)
                    {
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

        // Verify local version was preserved
        await using (var finalContext = await Factory.CreateDbContextAsync())
        {
            var item = await finalContext.TodoItems.FirstOrDefaultAsync(t => t.Id == sharedId);

            if (item is null)
            {
                throw new InvalidOperationException("Shared item not found after second patch");
            }

            if (item.Description != "Newer local modification")
            {
                throw new InvalidOperationException($"Local version should have been preserved, got: {item.Description}");
            }

            if (item.UpdatedAt != newerLocalTime)
            {
                throw new InvalidOperationException("Local UpdatedAt timestamp should be preserved");
            }
        }

        return "OK";
    }
}
