using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

/// <summary>
/// Tests BulkImportRawAsync with LastWriteWins conflict resolution.
/// Verifies that newer items overwrite older ones, and older items are skipped.
/// Both seed and delta use BulkImportRaw to ensure consistent DateTime format.
/// </summary>
internal class V2BulkRawImportConflictTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_Raw_Import_LWW";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var itemId = Guid.NewGuid();

        var columns = MessagePackFileHeaderV2.BuildColumnMetadata(typeof(TodoItemDto), TodoSqlTypeOverrides);
        var metadata = new BulkImportMetadata
        {
            TableName = "TodoItems",
            Columns = columns,
            PrimaryKeyColumn = "Id"
        };

        // Seed original item via BulkImportRaw (consistent DateTime format)
        var originalDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Original",
            Description = "Old",
            UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var seedData = MessagePackSerializer.Serialize(new List<TodoItemDto> { originalDto });
        await DatabaseService.BulkImportRawAsync("TestDb.db", metadata, seedData);

        // Import NEWER version with LastWriteWins — should overwrite
        var newerDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Updated via LWW",
            Description = "New",
            UpdatedAt = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc)
        };
        var newerData = MessagePackSerializer.Serialize(new List<TodoItemDto> { newerDto });
        await DatabaseService.BulkImportRawAsync("TestDb.db", metadata, newerData, ConflictResolutionStrategy.LastWriteWins);

        // Verify the newer version won
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var item = await ctx.TodoItems.IgnoreQueryFilters().FirstAsync(t => t.Id == itemId);
            if (item.Title != "Updated via LWW")
            {
                throw new InvalidOperationException($"LWW (newer): expected 'Updated via LWW', got '{item.Title}'");
            }
        }

        // Import OLDER version — should be skipped
        var olderDto = new TodoItemDto
        {
            Id = itemId,
            Title = "Should NOT appear",
            Description = "Very old",
            UpdatedAt = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var olderData = MessagePackSerializer.Serialize(new List<TodoItemDto> { olderDto });
        await DatabaseService.BulkImportRawAsync("TestDb.db", metadata, olderData, ConflictResolutionStrategy.LastWriteWins);

        // Verify the older version was skipped
        await using (var ctx2 = await Factory.CreateDbContextAsync())
        {
            var item = await ctx2.TodoItems.IgnoreQueryFilters().FirstAsync(t => t.Id == itemId);
            if (item.Title != "Updated via LWW")
            {
                throw new InvalidOperationException($"LWW (older): expected 'Updated via LWW', got '{item.Title}'");
            }
        }

        return "OK";
    }
}
