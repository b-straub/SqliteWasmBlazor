using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Helper methods for import/export tests
/// </summary>
internal static class ImportExportTestHelper
{
    private const int DefaultColumnCount = 6; // TodoItem: Id, Title, Description, IsCompleted, CreatedAt, CompletedAt

    /// <summary>
    /// Bulk insert TodoItemDtos using raw SQL to preserve IDs
    /// Same pattern as TodoImportExport.razor:175-216
    /// </summary>
    public static async Task BulkInsertTodoItemsAsync(TodoDbContext context, List<TodoItemDto> dtos)
    {
        if (dtos.Count == 0)
        {
            return;
        }

        // SQLite supports up to 999 parameters per statement
        // Try to get column count from EF Core metadata, fallback to constant
        const int maxSqliteParams = 999;
        var entityType = context.Model.FindEntityType(typeof(SqliteWasmBlazor.Models.Models.TodoItem));
        var columnCount = entityType?.GetProperties().Count() ?? DefaultColumnCount;
        var rowsPerBatch = maxSqliteParams / columnCount;

        for (var i = 0; i < dtos.Count; i += rowsPerBatch)
        {
            var batch = dtos.Skip(i).Take(rowsPerBatch).ToList();
            var valuesClauses = new List<string>();
            var parameters = new List<object?>();

            for (var j = 0; j < batch.Count; j++)
            {
                var dto = batch[j];
                var baseIndex = j * columnCount;
                valuesClauses.Add($"({{{baseIndex}}}, {{{baseIndex + 1}}}, {{{baseIndex + 2}}}, {{{baseIndex + 3}}}, {{{baseIndex + 4}}}, {{{baseIndex + 5}}})");

                parameters.Add(dto.Id);
                parameters.Add(dto.Title);
                parameters.Add(dto.Description);
                parameters.Add(dto.IsCompleted ? 1 : 0);
                parameters.Add(dto.CreatedAt.ToString("O"));
                parameters.Add(dto.CompletedAt?.ToString("O"));
            }

            var sql = $@"
                INSERT INTO TodoItems (Id, Title, Description, IsCompleted, CreatedAt, CompletedAt)
                VALUES {string.Join(", ", valuesClauses)}";

            await context.Database.ExecuteSqlRawAsync(sql, parameters.ToArray()!);
        }
    }
}
