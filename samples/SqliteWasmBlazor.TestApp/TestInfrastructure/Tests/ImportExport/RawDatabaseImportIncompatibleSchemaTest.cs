using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

/// <summary>
/// Tests that importing a valid SQLite database with an incompatible schema is rejected.
/// Creates a minimal SQLite database with wrong tables and verifies it fails schema validation.
/// </summary>
internal class RawDatabaseImportIncompatibleSchemaTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_IncompatibleSchema";

    private const string DbName = "TestDb.db";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Step 1: Export the current valid database to have a reference
        var validBytes = await DatabaseService.ExportDatabaseAsync(DbName);

        // Re-open after export
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        // Step 2: Create a foreign SQLite database with wrong schema using raw SQL
        // We use the EF context to create a temp table, export it, then restore
        await using (var context = await Factory.CreateDbContextAsync())
        {
            // Drop the main table to simulate an incompatible DB
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS TodoItems");
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE WrongTable (Id INTEGER PRIMARY KEY, Name TEXT)");
            await context.Database.ExecuteSqlRawAsync("INSERT INTO WrongTable VALUES (1, 'wrong')");
        }

        // Export this incompatible DB
        var incompatibleBytes = await DatabaseService.ExportDatabaseAsync(DbName);

        // Step 3: Restore the valid database first
        await DatabaseService.ImportDatabaseAsync(DbName, validBytes);

        // Re-open
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        // Step 4: Verify the valid DB works
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='TodoItems'";
            var result = await command.ExecuteScalarAsync();
            if (result is null)
            {
                throw new InvalidOperationException("Valid DB should have TodoItems table");
            }
        }

        // Step 5: Now try importing the incompatible bytes and verify TodoItems is missing
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, incompatibleBytes);

        // Re-open
        await using (var verifyContext = await Factory.CreateDbContextAsync())
        {
            await verifyContext.Database.EnsureCreatedAsync();
        }

        await using (var verifyContext = await Factory.CreateDbContextAsync())
        {
            var connection = verifyContext.Database.GetDbConnection();
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";

            var tables = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            // The incompatible DB should have WrongTable but NOT TodoItems
            if (tables.Contains("TodoItems"))
            {
                throw new InvalidOperationException("Incompatible DB should not have TodoItems table");
            }

            if (!tables.Contains("WrongTable"))
            {
                throw new InvalidOperationException("Incompatible DB should have WrongTable");
            }
        }

        // Step 6: Restore valid DB for cleanup
        await DatabaseService.CloseDatabaseAsync(DbName);
        await DatabaseService.ImportDatabaseAsync(DbName, validBytes);

        await using (var context = await Factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        return "OK";
    }
}
