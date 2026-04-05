using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

/// <summary>
/// Tests readonlyColumns validation: worker snapshots readonly columns before apply,
/// validates no mutations after, rolls back on violation.
/// </summary>
internal class V2BulkReadonlyColumnsAllowedTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_ReadonlyColumns_Allowed";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var itemId = Guid.NewGuid();

        // Seed original item
        var originalDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Buy milk",
            Description = "From store",
            IsCompleted = false,
            UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        await DatabaseService.BulkImportAsync("TestDb.db", BuildPayload([originalDto]));

        // Delta that changes IsCompleted only (Title stays the same)
        // Title is readonly — this should PASS because Title is unchanged
        var deltaDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Buy milk",        // unchanged
            Description = "From store", // unchanged
            IsCompleted = true,         // changed (allowed)
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var payload = BuildPayload([deltaDto]);
        var rowsImported = await DatabaseService.BulkImportAsync("TestDb.db", payload,
            ConflictResolutionStrategy.DeltaWins, readonlyColumns: new Dictionary<string, string[]> { ["TodoItems"] = ["Title"] });

        if (rowsImported != 1)
        {
            throw new InvalidOperationException($"Expected 1 row imported, got {rowsImported}");
        }

        // Verify IsCompleted was updated
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var item = await ctx.TodoItems.FirstAsync(t => t.Id == itemId);
            if (!item.IsCompleted)
            {
                throw new InvalidOperationException("IsCompleted should be true after delta");
            }

            if (item.Title != "Buy milk")
            {
                throw new InvalidOperationException($"Title should be unchanged, got '{item.Title}'");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems", primaryKeyColumn: "Id",
            recordCount: dtos.Count, mode: 1, sqlTypeOverrides: TodoSqlTypeOverrides);
        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos) { MessagePackSerializer.Serialize(ms, dto); }
        return ms.ToArray();
    }
}

/// <summary>
/// Tests that modifying a readonly column causes a rollback.
/// </summary>
internal class V2BulkReadonlyColumnsViolationTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_ReadonlyColumns_Violation";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var itemId = Guid.NewGuid();

        // Seed original item
        var originalDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Buy milk",
            Description = "From store",
            IsCompleted = false,
            UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        await DatabaseService.BulkImportAsync("TestDb.db", BuildPayload([originalDto]));

        // Delta that changes Title (readonly) — should be REJECTED
        var deltaDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Forged title",     // MUTATED — violation!
            Description = "From store",
            IsCompleted = true,
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var payload = BuildPayload([deltaDto]);

        try
        {
            await DatabaseService.BulkImportAsync("TestDb.db", payload,
                ConflictResolutionStrategy.DeltaWins, readonlyColumns: new Dictionary<string, string[]> { ["TodoItems"] = ["Title"] });

            throw new InvalidOperationException("Expected readonly violation but import succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Readonly column violation") || ex.Message.Contains("readonly"))
        {
            // Expected — violation detected, transaction rolled back
        }

        // Verify rollback — original data preserved
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var item = await ctx.TodoItems.FirstAsync(t => t.Id == itemId);

            if (item.Title != "Buy milk")
            {
                throw new InvalidOperationException($"Rollback failed: Title is '{item.Title}', expected 'Buy milk'");
            }

            if (item.IsCompleted)
            {
                throw new InvalidOperationException("Rollback failed: IsCompleted should still be false");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems", primaryKeyColumn: "Id",
            recordCount: dtos.Count, mode: 1, sqlTypeOverrides: TodoSqlTypeOverrides);
        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos) { MessagePackSerializer.Serialize(ms, dto); }
        return ms.ToArray();
    }
}

/// <summary>
/// Tests mixed delta: valid update + readonly violation in same batch → entire rollback.
/// </summary>
internal class V2BulkReadonlyColumnsMixedViolationTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_ReadonlyColumns_MixedViolation";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();

        // Seed two items
        await DatabaseService.BulkImportAsync("TestDb.db", BuildPayload([
            new TodoItemDto { Id = item1Id, Title = "Milk", IsCompleted = false, UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new TodoItemDto { Id = item2Id, Title = "Eggs", IsCompleted = false, UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
        ]));

        // Delta: item1 valid (only IsCompleted changed), item2 violation (Title changed)
        var payload = BuildPayload([
            new TodoItemDto { Id = item1Id, Title = "Milk", IsCompleted = true, UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new TodoItemDto { Id = item2Id, Title = "Forged!", IsCompleted = true, UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
        ]);

        try
        {
            await DatabaseService.BulkImportAsync("TestDb.db", payload,
                ConflictResolutionStrategy.DeltaWins,
                readonlyColumns: new Dictionary<string, string[]> { ["TodoItems"] = ["Title"] });

            throw new InvalidOperationException("Expected readonly violation but import succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Readonly") || ex.Message.Contains("readonly"))
        {
            // Expected
        }

        // Verify BOTH items rolled back — neither updated
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var item1 = await ctx.TodoItems.FirstAsync(t => t.Id == item1Id);
            var item2 = await ctx.TodoItems.FirstAsync(t => t.Id == item2Id);

            if (item1.IsCompleted)
            {
                throw new InvalidOperationException("Rollback failed: item1 IsCompleted should still be false");
            }

            if (item2.Title != "Eggs")
            {
                throw new InvalidOperationException($"Rollback failed: item2 Title should be 'Eggs', got '{item2.Title}'");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems", primaryKeyColumn: "Id",
            recordCount: dtos.Count, mode: 1, sqlTypeOverrides: TodoSqlTypeOverrides);
        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos) { MessagePackSerializer.Serialize(ms, dto); }
        return ms.ToArray();
    }
}

/// <summary>
/// Tests mixed delta: valid update + new row insert → entire rollback (new rows rejected with readonly).
/// </summary>
internal class V2BulkReadonlyColumnsMixedNewRowTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_ReadonlyColumns_MixedNewRow";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var existingId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        // Seed one item
        await DatabaseService.BulkImportAsync("TestDb.db", BuildPayload([
            new TodoItemDto { Id = existingId, Title = "Milk", IsCompleted = false, UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
        ]));

        // Delta: valid update on existing + new row insert → rejected
        var payload = BuildPayload([
            new TodoItemDto { Id = existingId, Title = "Milk", IsCompleted = true, UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new TodoItemDto { Id = newId, Title = "Smuggled item", IsCompleted = false, UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
        ]);

        try
        {
            await DatabaseService.BulkImportAsync("TestDb.db", payload,
                ConflictResolutionStrategy.DeltaWins,
                readonlyColumns: new Dictionary<string, string[]> { ["TodoItems"] = ["Title"] });

            throw new InvalidOperationException("Expected readonly violation for new row but import succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Readonly") || ex.Message.Contains("readonly"))
        {
            // Expected
        }

        // Verify rollback — existing item unchanged, new item not inserted
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var existing = await ctx.TodoItems.FirstAsync(t => t.Id == existingId);
            if (existing.IsCompleted)
            {
                throw new InvalidOperationException("Rollback failed: existing item should still be incomplete");
            }

            var newItem = await ctx.TodoItems.FirstOrDefaultAsync(t => t.Id == newId);
            if (newItem is not null)
            {
                throw new InvalidOperationException("Rollback failed: new item should not exist");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems", primaryKeyColumn: "Id",
            recordCount: dtos.Count, mode: 1, sqlTypeOverrides: TodoSqlTypeOverrides);
        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos) { MessagePackSerializer.Serialize(ms, dto); }
        return ms.ToArray();
    }
}

/// <summary>
/// Tests that new rows (INSERT) are rejected when readonlyColumns is set.
/// A sender with readonly restrictions cannot create new rows.
/// </summary>
internal class V2BulkReadonlyColumnsNewRowRejectedTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_ReadonlyColumns_NewRowRejected";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Try to insert a brand new row with readonlyColumns set — should be REJECTED
        // (sender with readonly restrictions can't create new rows)
        var dto = new TodoItemDto
        {
            Id = Guid.NewGuid(),
            Title = "Forged new item",
            Description = "Should not appear",
            IsCompleted = false,
            UpdatedAt = DateTime.UtcNow
        };

        var payload = BuildPayload([dto]);

        try
        {
            await DatabaseService.BulkImportAsync("TestDb.db", payload,
                ConflictResolutionStrategy.DeltaWins, readonlyColumns: new Dictionary<string, string[]> { ["TodoItems"] = ["Title"] });

            throw new InvalidOperationException("Expected readonly violation for new row but import succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("readonly") || ex.Message.Contains("Readonly"))
        {
            // Expected — new row with readonly columns rejected
        }

        // Verify rollback — no new row
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var count = await ctx.TodoItems.CountAsync(t => t.Id == dto.Id);
            if (count != 0)
            {
                throw new InvalidOperationException("Rollback failed: new row should not exist");
            }
        }

        return "OK";
    }

    private static byte[] BuildPayload(List<TodoItemDto> dtos)
    {
        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems", primaryKeyColumn: "Id",
            recordCount: dtos.Count, mode: 1, sqlTypeOverrides: TodoSqlTypeOverrides);
        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, header);
        foreach (var dto in dtos) { MessagePackSerializer.Serialize(ms, dto); }
        return ms.ToArray();
    }
}
