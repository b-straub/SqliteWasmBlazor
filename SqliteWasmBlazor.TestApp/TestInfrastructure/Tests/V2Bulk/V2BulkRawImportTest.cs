using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

/// <summary>
/// Tests BulkImportRawAsync: import from plain MessagePack List&lt;TDto&gt; bytes
/// with metadata provided separately (no V2 header in payload).
/// This is the format used by sync services like BlazPulse.
/// </summary>
internal class V2BulkRawImportTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_Raw_Import";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Create test items and serialize as plain List<TodoItemDto> (sync format)
        var items = new List<TodoItemDto>
        {
            TodoItemDto.FromEntity(new TodoItem { Id = Guid.NewGuid(), Title = "Raw First", Description = "Desc 1", UpdatedAt = DateTime.UtcNow }),
            TodoItemDto.FromEntity(new TodoItem { Id = Guid.NewGuid(), Title = "Raw Second", Description = "Desc 2", UpdatedAt = DateTime.UtcNow, IsCompleted = true, CompletedAt = DateTime.UtcNow }),
            TodoItemDto.FromEntity(new TodoItem { Id = Guid.NewGuid(), Title = "Raw Third 🚀", Description = "Unicode", UpdatedAt = DateTime.UtcNow })
        };

        // Serialize as plain List<TDto> — exactly how BlazPulse's TableSyncHandler does it
        var rowData = MessagePackSerializer.Serialize(items);

        // Build metadata from DTO type (no V2 header needed in payload)
        var columns = MessagePackFileHeaderV2.BuildColumnMetadata(typeof(TodoItemDto), TodoSqlTypeOverrides);

        var metadata = new BulkImportMetadata
        {
            TableName = "TodoItems",
            Columns = columns,
            PrimaryKeyColumn = "Id"
        };

        // Clear table first
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
        }

        // BulkImportRaw — no V2 header, just metadata + plain MessagePack bytes
        var rowsImported = await DatabaseService.BulkImportRawAsync(
            "TestDb.db", metadata, rowData, ConflictResolutionStrategy.None);

        if (rowsImported != 3)
        {
            throw new InvalidOperationException($"Expected 3 rows imported, got {rowsImported}");
        }

        // Verify data integrity
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var retrieved = await ctx.TodoItems.IgnoreQueryFilters().OrderBy(t => t.Title).ToListAsync();
            if (retrieved.Count != 3)
            {
                throw new InvalidOperationException($"Expected 3 items in DB, got {retrieved.Count}");
            }

            foreach (var original in items)
            {
                var match = retrieved.FirstOrDefault(r => r.Id == original.Id);
                if (match is null)
                {
                    throw new InvalidOperationException($"Item {original.Id} not found after raw import");
                }

                if (match.Title != original.Title)
                {
                    throw new InvalidOperationException($"Title mismatch: '{original.Title}' vs '{match.Title}'");
                }

                if (match.IsCompleted != original.IsCompleted)
                {
                    throw new InvalidOperationException($"IsCompleted mismatch for {original.Id}");
                }
            }
        }

        return "OK";
    }
}
