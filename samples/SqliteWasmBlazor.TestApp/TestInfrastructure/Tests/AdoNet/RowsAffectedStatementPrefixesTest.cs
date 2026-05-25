using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class RowsAffectedStatementPrefixesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_RowsAffectedStatementPrefixes";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE RowsAffectedItems (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE,
                Quantity INTEGER NOT NULL
            )
            """);

        var insertedWithBlockComment = await ExecuteAsync(connection, """
            /* leading block comment */
            INSERT INTO RowsAffectedItems (Id, Name, Quantity)
            VALUES (1, 'Alpha', 1)
            """);
        AssertRowsAffected(1, insertedWithBlockComment, "comment-prefixed INSERT");

        var insertedWithLineComment = await ExecuteAsync(connection, """
            -- leading line comment
            INSERT INTO RowsAffectedItems (Id, Name, Quantity)
            VALUES (2, 'Beta', 2)
            """);
        AssertRowsAffected(1, insertedWithLineComment, "comment-prefixed INSERT after line comment");

        var updatedWithCte = await ExecuteAsync(connection, """
            WITH target AS (
                SELECT Id
                FROM RowsAffectedItems
                WHERE Name = 'Beta'
            )
            UPDATE RowsAffectedItems
            SET Quantity = Quantity + 3
            WHERE Id IN (SELECT Id FROM target)
            """);
        AssertRowsAffected(1, updatedWithCte, "CTE-prefixed UPDATE");

        var replaced = await ExecuteAsync(connection, """
            REPLACE INTO RowsAffectedItems (Id, Name, Quantity)
            VALUES (1, 'Alpha', 10)
            """);
        AssertRowsAffected(1, replaced, "REPLACE");

        var insertedWithCte = await ExecuteAsync(connection, """
            WITH incoming(Name, Quantity) AS (
                VALUES ('Gamma', 7)
            )
            INSERT INTO RowsAffectedItems (Name, Quantity)
            SELECT Name, Quantity
            FROM incoming
            """);
        AssertRowsAffected(1, insertedWithCte, "CTE-prefixed INSERT");

        var deletedWithCte = await ExecuteAsync(connection, """
            WITH target AS (
                SELECT Id
                FROM RowsAffectedItems
                WHERE Name = 'Gamma'
            )
            DELETE FROM RowsAffectedItems
            WHERE Id IN (SELECT Id FROM target)
            """);
        AssertRowsAffected(1, deletedWithCte, "CTE-prefixed DELETE");

        var count = Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM RowsAffectedItems"));
        if (count != 2)
        {
            throw new InvalidOperationException($"Expected two remaining rows, got {count}.");
        }

        var betaQuantity = Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT Quantity FROM RowsAffectedItems WHERE Name = 'Beta'"));
        if (betaQuantity != 5)
        {
            throw new InvalidOperationException($"Expected Beta quantity 5, got {betaQuantity}.");
        }

        var alphaQuantity = Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT Quantity FROM RowsAffectedItems WHERE Name = 'Alpha'"));
        if (alphaQuantity != 10)
        {
            throw new InvalidOperationException($"Expected Alpha quantity 10, got {alphaQuantity}.");
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

    private static void AssertRowsAffected(int expected, int actual, string operation)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Expected {operation} to affect {expected} row(s), got {actual}.");
        }
    }
}
