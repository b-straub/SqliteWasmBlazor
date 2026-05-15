using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests import followed by export: verifies the full round-trip
/// where an imported database can be immediately re-exported.
/// </summary>
internal class RawDatabaseImportThenExportTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportExportRawDatabase_ImportThenExport";

    private const string DbName = "TestDb.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Create data and export as reference
        var originalId = Guid.NewGuid();
        await using (var context = await Factory.CreateDbContextAsync())
        {
            context.TodoItems.Add(new TodoItem
            {
                Id = originalId, Title = "Round Trip Item", Description = "Survives import then export",
                IsCompleted = true, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var originalExport = await DatabaseService.ExportDatabaseAsync(DbName);

        // Clear the DB and import the export
        await DatabaseService.DeleteDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, originalExport);

        // Immediately export again (import → export chain)
        var reExport = await DatabaseService.ExportDatabaseAsync(DbName);

        if (reExport.Length == 0)
        {
            throw new InvalidOperationException("Re-export after import returned empty data");
        }

        // Import the re-export into a clean DB and verify data integrity
        await DatabaseService.DeleteDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, reExport);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();

            var count = await context.TodoItems.CountAsync();
            if (count != 1)
            {
                throw new InvalidOperationException($"Expected 1 item after re-import, got {count}");
            }

            var item = await context.TodoItems.FirstOrDefaultAsync(t => t.Id == originalId);
            if (item is null || item.Title != "Round Trip Item" || !item.IsCompleted || item.CompletedAt is null)
            {
                throw new InvalidOperationException("Data mismatch after import → export → import chain");
            }
        }

        return "OK";
    }
}
