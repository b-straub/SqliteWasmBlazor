using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class EmptyReaderMetadataTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_EmptyReaderMetadata";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE EmptyMetadataItems (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Quantity INTEGER NOT NULL,
                Price REAL NOT NULL,
                Payload BLOB NULL
            )
            """);

        await using (var emptySelect = connection.CreateCommand())
        {
            emptySelect.CommandText = """
                SELECT Id, Name, Quantity, Price, Payload
                FROM EmptyMetadataItems
                WHERE Id = -1
                """;

            await using var reader = await emptySelect.ExecuteReaderAsync();
            AssertMetadata(
                reader,
                ["Id", "Name", "Quantity", "Price", "Payload"],
                ["INTEGER", "TEXT", "INTEGER", "REAL", "BLOB"],
                "empty SELECT");

            if (reader.HasRows)
            {
                throw new InvalidOperationException("Empty SELECT unexpectedly reported rows.");
            }

            if (await reader.ReadAsync())
            {
                throw new InvalidOperationException("Empty SELECT unexpectedly returned a row.");
            }
        }

        await using (var emptyReturning = connection.CreateCommand())
        {
            emptyReturning.CommandText = """
                UPDATE EmptyMetadataItems
                SET Quantity = Quantity + 1
                WHERE Id = -1
                RETURNING Id, Name, Quantity
                """;

            await using var reader = await emptyReturning.ExecuteReaderAsync();
            AssertMetadata(
                reader,
                ["Id", "Name", "Quantity"],
                ["INTEGER", "TEXT", "INTEGER"],
                "empty UPDATE RETURNING");

            if (reader.RecordsAffected != 0)
            {
                throw new InvalidOperationException(
                    $"Expected empty UPDATE RETURNING RecordsAffected=0, got {reader.RecordsAffected}.");
            }

            if (await reader.ReadAsync())
            {
                throw new InvalidOperationException("Empty UPDATE RETURNING unexpectedly returned a row.");
            }
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

    private static void AssertMetadata(
        System.Data.Common.DbDataReader reader,
        string[] expectedNames,
        string[] expectedTypes,
        string operation)
    {
        if (reader.FieldCount != expectedNames.Length)
        {
            throw new InvalidOperationException(
                $"{operation} FieldCount failed: expected {expectedNames.Length}, got {reader.FieldCount}.");
        }

        for (var i = 0; i < expectedNames.Length; i++)
        {
            if (reader.GetName(i) != expectedNames[i] ||
                reader.GetDataTypeName(i) != expectedTypes[i])
            {
                throw new InvalidOperationException(
                    $"{operation} metadata failed at ordinal {i}: " +
                    $"{reader.GetName(i)} / {reader.GetDataTypeName(i)}.");
            }
        }
    }
}
