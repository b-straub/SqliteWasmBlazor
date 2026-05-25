using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ConstraintConflictHandlingTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ConstraintConflictHandling";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE ConflictItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Sku TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                Quantity INTEGER NOT NULL DEFAULT 0
            )
            """);

        var inserted = await ExecuteAsync(
            connection,
            "INSERT INTO ConflictItems (Sku, Name, Quantity) VALUES ('A-001', 'Alpha', 1)");
        AssertRowsAffected(1, inserted, "initial insert");

        await AssertSqlFailsAsync(
            connection,
            "INSERT INTO ConflictItems (Sku, Name, Quantity) VALUES ('A-001', 'Duplicate', 2)",
            "UNIQUE");

        await AssertSqlFailsAsync(
            connection,
            "INSERT INTO ConflictItems (Sku, Name, Quantity) VALUES ('A-002', NULL, 1)",
            "NOT NULL");

        var ignored = await ExecuteAsync(
            connection,
            "INSERT OR IGNORE INTO ConflictItems (Sku, Name, Quantity) VALUES ('A-001', 'Ignored', 3)");
        AssertRowsAffected(0, ignored, "INSERT OR IGNORE duplicate");

        var upserted = await ExecuteAsync(connection, """
            INSERT INTO ConflictItems (Sku, Name, Quantity)
            VALUES ('A-001', 'Updated', 4)
            ON CONFLICT(Sku) DO UPDATE SET
                Name = excluded.Name,
                Quantity = ConflictItems.Quantity + excluded.Quantity
            """);
        AssertRowsAffected(1, upserted, "ON CONFLICT DO UPDATE");

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT Name, Quantity
            FROM ConflictItems
            WHERE Sku = 'A-001'
            """;

        await using var reader = await query.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected conflict test row.");
        }

        if (reader.GetString(0) != "Updated" || reader.GetInt32(1) != 5)
        {
            throw new InvalidOperationException("Upsert did not preserve native SQLite conflict behavior.");
        }

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one conflict test row.");
        }

        var rowCount = Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM ConflictItems"));
        if (rowCount != 1)
        {
            throw new InvalidOperationException($"Expected one row after ignored duplicate, got {rowCount}.");
        }

        return "OK";
    }

    private static async Task<int> ExecuteAsync(
        System.Data.Common.DbConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(
        System.Data.Common.DbConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task AssertSqlFailsAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        string expectedMessage)
    {
        try
        {
            await ExecuteAsync(connection, sql);
        }
        catch (Exception ex) when (ContainsMessage(ex, expectedMessage))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected SQL to fail with '{expectedMessage}', but it succeeded.");
    }

    private static bool ContainsMessage(Exception exception, string expected)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertRowsAffected(int expected, int actual, string operation)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Expected {operation} to affect {expected} row(s), got {actual}.");
        }
    }
}
