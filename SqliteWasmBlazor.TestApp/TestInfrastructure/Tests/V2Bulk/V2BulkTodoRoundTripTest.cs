using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

/// <summary>
/// V2 bulk round-trip for TodoItem (Guid stored as BLOB via sqlTypeOverride).
/// </summary>
internal class V2BulkTodoRoundTripTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_Todo_RoundTrip";

    private static readonly Dictionary<string, string> TodoSqlTypeOverrides = new() { ["Id"] = "BLOB" };

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Insert 3 TodoItems via EF Core
        var items = new List<TodoItem>
        {
            new() { Id = Guid.NewGuid(), Title = "First", Description = "Desc 1", UpdatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), Title = "Second", Description = "Desc 2", UpdatedAt = DateTime.UtcNow, IsCompleted = true, CompletedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), Title = "Third with émojis 🎉", Description = "Unicode test", UpdatedAt = DateTime.UtcNow }
        };

        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            ctx.TodoItems.AddRange(items);
            await ctx.SaveChangesAsync();
        }

        // BulkExport with BLOB override for Guid Id
        var columns = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems",
            primaryKeyColumn: "Id",
            recordCount: 0,
            sqlTypeOverrides: TodoSqlTypeOverrides).Columns;

        var exportMetadata = new BulkExportMetadata
        {
            TableName = "TodoItems",
            Columns = columns,
            PrimaryKeyColumn = "Id",
            SchemaHash = SchemaHashGenerator.ComputeHash<TodoItemDto>(),
            DataType = typeof(TodoItemDto).FullName ?? typeof(TodoItemDto).Name,
            Mode = 0,
            Where = "\"IsDeleted\" = 0",
            OrderBy = "\"UpdatedAt\" DESC"
        };

        var exportedBytes = await DatabaseService.BulkExportAsync("TestDb.db", exportMetadata);

        // Clear table
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM TodoItems");
        }

        // BulkImport
        var rowsImported = await DatabaseService.BulkImportAsync("TestDb.db", exportedBytes);
        if (rowsImported != 3)
        {
            throw new InvalidOperationException($"Expected 3 rows, got {rowsImported}");
        }

        // Verify
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var retrieved = await ctx.TodoItems.OrderBy(t => t.Title).ToListAsync();
            if (retrieved.Count != 3)
            {
                throw new InvalidOperationException($"Expected 3 items, got {retrieved.Count}");
            }

            foreach (var original in items)
            {
                var match = retrieved.FirstOrDefault(r => r.Id == original.Id);
                if (match is null)
                {
                    throw new InvalidOperationException($"Item {original.Id} not found after import");
                }

                if (match.Title != original.Title)
                {
                    throw new InvalidOperationException($"Title mismatch for {original.Id}: '{original.Title}' vs '{match.Title}'");
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
