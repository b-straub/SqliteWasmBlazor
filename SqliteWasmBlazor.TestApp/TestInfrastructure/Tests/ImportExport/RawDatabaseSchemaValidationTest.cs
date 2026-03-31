using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Extensions;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests the ValidateSchemaAsync extension method that uses EF model metadata
/// to derive expected table names. Verifies it passes for valid databases
/// and throws InvalidOperationException for incompatible ones.
/// </summary>
internal class RawDatabaseSchemaValidationTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_SchemaValidationExtension";

    private const string DbName = "TestDb.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Test 1: Valid database passes validation
        await using (var context = await Factory.CreateDbContextAsync())
        {
            // Should not throw — the fresh test DB has all required tables
            await context.ValidateImportedSchemaAsync(DbName);
        }

        // Test 2: Export valid DB, import it, validate passes
        var validBytes = await DatabaseService.ExportDatabaseAsync(DbName);

        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, validBytes);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.ValidateImportedSchemaAsync(DbName);
        }

        // Test 3: Create incompatible DB (drop a required table), export, then validate fails
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS TodoItems");
        }

        var incompatibleBytes = await DatabaseService.ExportDatabaseAsync(DbName);

        // Restore valid DB first
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, validBytes);

        // Now import the incompatible one
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, incompatibleBytes);

        try
        {
            await using var validateCtx = await Factory.CreateDbContextAsync();
            await validateCtx.ValidateImportedSchemaAsync(DbName);
            throw new InvalidOperationException("Expected InvalidOperationException for incompatible schema but validation passed");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("missing tables"))
        {
            // Expected — the error message should mention the missing table
            if (!ex.Message.Contains("TodoItems"))
            {
                throw new InvalidOperationException($"Error should mention 'TodoItems' but got: {ex.Message}");
            }
        }

        // Restore valid DB for cleanup
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, validBytes);

        // Verify restored DB works
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.TodoItems.CountAsync();
        }

        return "OK";
    }
}
