using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ReturningClausesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ReturningClauses";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE ReturningItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Quantity INTEGER NOT NULL
            )
            """);

        await using (var insertScalar = connection.CreateCommand())
        {
            insertScalar.CommandText = """
                INSERT INTO ReturningItems (Name, Quantity)
                VALUES ('Alpha', 2)
                RETURNING Id
                """;

            var id = Convert.ToInt32(await insertScalar.ExecuteScalarAsync());
            if (id != 1)
            {
                throw new InvalidOperationException($"Expected scalar INSERT RETURNING id 1, got {id}.");
            }
        }

        await using (var insertReader = connection.CreateCommand())
        {
            insertReader.CommandText = """
                INSERT INTO ReturningItems (Name, Quantity)
                VALUES ('Beta', 5), ('Gamma', 8)
                RETURNING Id, Name, Quantity
                """;

            await using var reader = await insertReader.ExecuteReaderAsync();
            if (reader.RecordsAffected != 2)
            {
                throw new InvalidOperationException(
                    $"Expected INSERT RETURNING RecordsAffected=2, got {reader.RecordsAffected}.");
            }

            AssertColumn(reader, 0, "Id", "INTEGER");
            AssertColumn(reader, 1, "Name", "TEXT");
            AssertColumn(reader, 2, "Quantity", "INTEGER");

            await AssertReturningRowAsync(reader, 2, "Beta", 5);
            await AssertReturningRowAsync(reader, 3, "Gamma", 8);

            if (await reader.ReadAsync())
            {
                throw new InvalidOperationException("Expected only two INSERT RETURNING rows.");
            }
        }

        await using (var updateReader = connection.CreateCommand())
        {
            updateReader.CommandText = """
                UPDATE ReturningItems
                SET Quantity = Quantity + 10
                WHERE Name IN ('Beta', 'Gamma')
                RETURNING Name, Quantity
                """;

            await using var reader = await updateReader.ExecuteReaderAsync();
            if (reader.RecordsAffected != 2)
            {
                throw new InvalidOperationException(
                    $"Expected UPDATE RETURNING RecordsAffected=2, got {reader.RecordsAffected}.");
            }

            await AssertReturningRowAsync(reader, "Beta", 15);
            await AssertReturningRowAsync(reader, "Gamma", 18);
        }

        await using (var deleteReader = connection.CreateCommand())
        {
            deleteReader.CommandText = """
                WITH deleted AS (
                    SELECT Id
                    FROM ReturningItems
                    WHERE Name = 'Alpha'
                )
                DELETE FROM ReturningItems
                WHERE Id IN (SELECT Id FROM deleted)
                RETURNING Name, Quantity
                """;

            await using var reader = await deleteReader.ExecuteReaderAsync();
            if (reader.RecordsAffected != 1)
            {
                throw new InvalidOperationException(
                    $"Expected DELETE RETURNING RecordsAffected=1, got {reader.RecordsAffected}.");
            }

            await AssertReturningRowAsync(reader, "Alpha", 2);
            if (await reader.ReadAsync())
            {
                throw new InvalidOperationException("Expected only one DELETE RETURNING row.");
            }
        }

        var remaining = Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM ReturningItems"));
        if (remaining != 2)
        {
            throw new InvalidOperationException($"Expected two remaining rows, got {remaining}.");
        }

        return "OK";
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(
        System.Data.Common.DbConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task AssertReturningRowAsync(
        System.Data.Common.DbDataReader reader,
        int expectedId,
        string expectedName,
        int expectedQuantity)
    {
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected another RETURNING row.");
        }

        if (reader.GetInt32(0) != expectedId ||
            reader.GetString(1) != expectedName ||
            reader.GetInt32(2) != expectedQuantity)
        {
            throw new InvalidOperationException("RETURNING row values did not match expected values.");
        }
    }

    private static async Task AssertReturningRowAsync(
        System.Data.Common.DbDataReader reader,
        string expectedName,
        int expectedQuantity)
    {
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected another RETURNING row.");
        }

        if (reader.GetString(0) != expectedName ||
            reader.GetInt32(1) != expectedQuantity)
        {
            throw new InvalidOperationException("RETURNING row values did not match expected values.");
        }
    }

    private static void AssertColumn(
        System.Data.Common.DbDataReader reader,
        int ordinal,
        string expectedName,
        string expectedType)
    {
        if (reader.GetName(ordinal) != expectedName ||
            reader.GetDataTypeName(ordinal) != expectedType)
        {
            throw new InvalidOperationException(
                $"Unexpected RETURNING column metadata at ordinal {ordinal}: " +
                $"{reader.GetName(ordinal)} / {reader.GetDataTypeName(ordinal)}.");
        }
    }
}
