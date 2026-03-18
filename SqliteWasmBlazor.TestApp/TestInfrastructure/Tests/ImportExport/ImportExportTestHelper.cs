using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Helper methods for import/export tests
/// </summary>
internal static class ImportExportTestHelper
{
    private const int DefaultColumnCount = 8; // TodoItem: Id, Title, Description, IsCompleted, UpdatedAt, CompletedAt, IsDeleted, DeletedAt

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
        // Use the actual number of columns in the INSERT, not total entity properties
        // (TodoItem has 8 properties but we only insert 6: IsDeleted/DeletedAt default to false/null)
        const int maxSqliteParams = 999;
        var rowsPerBatch = maxSqliteParams / DefaultColumnCount;

        for (var i = 0; i < dtos.Count; i += rowsPerBatch)
        {
            var batch = dtos.Skip(i).Take(rowsPerBatch).ToList();
            var valuesClauses = new List<string>();
            var parameters = new List<object?>();

            for (var j = 0; j < batch.Count; j++)
            {
                var dto = batch[j];
                var baseIndex = j * DefaultColumnCount;
                valuesClauses.Add($"(@p{baseIndex}, @p{baseIndex + 1}, @p{baseIndex + 2}, @p{baseIndex + 3}, @p{baseIndex + 4}, @p{baseIndex + 5}, @p{baseIndex + 6}, @p{baseIndex + 7})");

                parameters.Add(dto.Id);
                parameters.Add(dto.Title);
                parameters.Add(dto.Description);
                parameters.Add(dto.IsCompleted ? 1 : 0);
                parameters.Add(dto.UpdatedAt.ToString("O"));
                parameters.Add(dto.CompletedAt?.ToString("O"));
                parameters.Add(0); // IsDeleted = false
                parameters.Add(null); // DeletedAt = null
            }

            var sql = $@"
                INSERT INTO TodoItems (Id, Title, Description, IsCompleted, UpdatedAt, CompletedAt, IsDeleted, DeletedAt)
                VALUES {string.Join(", ", valuesClauses)}";

            await context.Database.ExecuteSqlRawAsync(sql, parameters.ToArray()!);
        }
    }
}
