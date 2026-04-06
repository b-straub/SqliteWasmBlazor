using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

/// <summary>
/// Tests all ConflictResolutionStrategy values through the V2 bulk import path.
/// Verifies ON CONFLICT SQL clauses work correctly in the worker's prepared statement loop.
/// </summary>
internal class V2BulkConflictLastWriteWinsTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_Conflict_LastWriteWins";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var itemId = Guid.NewGuid();

        // Insert original item via BulkImport (bypasses UpdatedAtInterceptor)
        var originalDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Original",
            Description = "Original desc",
            UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var seedPayload = BuildPayload([originalDto]);
        await DatabaseService.BulkImportAsync("TestDb.db", seedPayload);

        // Create V2 payload with NEWER timestamp — should overwrite
        var newerDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Updated by delta",
            Description = "Newer desc",
            UpdatedAt = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc)
        };

        var payload = BuildPayload([newerDto]);
        await DatabaseService.BulkImportAsync("TestDb.db", payload, ConflictResolutionStrategy.LastWriteWins);

        // Verify: newer wins
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var item = await ctx.TodoItems.FirstAsync(t => t.Id == itemId);
            if (item.Title != "Updated by delta")
            {
                throw new InvalidOperationException($"LastWriteWins (newer): expected 'Updated by delta', got '{item.Title}'");
            }
        }

        // Now try with OLDER timestamp — should NOT overwrite
        var olderDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Should not appear",
            Description = "Older desc",
            UpdatedAt = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var olderPayload = BuildPayload([olderDto]);
        await DatabaseService.BulkImportAsync("TestDb.db", olderPayload, ConflictResolutionStrategy.LastWriteWins);

        // Verify: older does NOT win
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var item = await ctx.TodoItems.FirstAsync(t => t.Id == itemId);
            if (item.Title != "Updated by delta")
            {
                throw new InvalidOperationException($"LastWriteWins (older): expected 'Updated by delta', got '{item.Title}'");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems",
            primaryKeyColumn: "Id",
            recordCount: dtos.Count,
            mode: 1,
            sqlTypeOverrides: TodoSqlTypeOverrides);

        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos)
        {
            MessagePackSerializer.Serialize(ms, dto);
        }

        return ms.ToArray();
    }
}

internal class V2BulkConflictLocalWinsTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_Conflict_LocalWins";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var existingId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        // Insert original item via BulkImport (bypasses UpdatedAtInterceptor)
        var originalDto = new TodoItemDto
        {
            Id = existingId,
            Title = "Local version",
            Description = "Should be preserved",
            UpdatedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var seedPayload = BuildPayload([originalDto]);
        await DatabaseService.BulkImportAsync("TestDb.db", seedPayload);

        // Import with LocalWins: existing item should NOT be updated, new item should be inserted
        var dtos = new List<TodoItemDto>
        {
            new()
            {
                Id = existingId,
                Title = "Delta version — should be ignored",
                Description = "Should not appear",
                UpdatedAt = DateTime.UtcNow.AddHours(1)
            },
            new()
            {
                Id = newId,
                Title = "New item from delta",
                Description = "Should be inserted",
                UpdatedAt = DateTime.UtcNow
            }
        };

        var payload = BuildPayload(dtos);
        await DatabaseService.BulkImportAsync("TestDb.db", payload, ConflictResolutionStrategy.LocalWins);

        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            // Existing item: local wins, title preserved
            var existing = await ctx.TodoItems.FirstAsync(t => t.Id == existingId);
            if (existing.Title != "Local version")
            {
                throw new InvalidOperationException($"LocalWins: expected 'Local version', got '{existing.Title}'");
            }

            // New item: should be inserted
            var newItem = await ctx.TodoItems.FirstOrDefaultAsync(t => t.Id == newId);
            if (newItem is null)
            {
                throw new InvalidOperationException("LocalWins: new item was not inserted");
            }

            if (newItem.Title != "New item from delta")
            {
                throw new InvalidOperationException($"LocalWins: new item title mismatch: '{newItem.Title}'");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems",
            primaryKeyColumn: "Id",
            recordCount: dtos.Count,
            mode: 1,
            sqlTypeOverrides: TodoSqlTypeOverrides);

        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos)
        {
            MessagePackSerializer.Serialize(ms, dto);
        }

        return ms.ToArray();
    }
}

internal class V2BulkConflictDeltaWinsTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_Conflict_DeltaWins";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var itemId = Guid.NewGuid();

        // Insert original via BulkImport (bypasses UpdatedAtInterceptor)
        var originalDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Original",
            Description = "Original desc",
            IsCompleted = false,
            UpdatedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var seedPayload = BuildPayload([originalDto]);
        await DatabaseService.BulkImportAsync("TestDb.db", seedPayload);

        // Import with DeltaWins — should ALWAYS overwrite, regardless of timestamp
        var dto = new TodoItemDto
        {
            Id = itemId,
            Title = "Delta always wins",
            Description = "Overwritten",
            IsCompleted = true,
            UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) // Even with older timestamp
        };

        var payload = BuildPayload([dto]);
        await DatabaseService.BulkImportAsync("TestDb.db", payload, ConflictResolutionStrategy.DeltaWins);

        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var item = await ctx.TodoItems.FirstAsync(t => t.Id == itemId);

            if (item.Title != "Delta always wins")
            {
                throw new InvalidOperationException($"DeltaWins: expected 'Delta always wins', got '{item.Title}'");
            }

            if (!item.IsCompleted)
            {
                throw new InvalidOperationException("DeltaWins: IsCompleted should be true");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems",
            primaryKeyColumn: "Id",
            recordCount: dtos.Count,
            mode: 1,
            sqlTypeOverrides: TodoSqlTypeOverrides);

        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos)
        {
            MessagePackSerializer.Serialize(ms, dto);
        }

        return ms.ToArray();
    }
}
