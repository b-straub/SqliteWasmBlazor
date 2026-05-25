using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ParameterPrefixCompatibilityTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ParameterPrefixCompatibility";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE PrefixItems (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Quantity INTEGER NOT NULL,
                Payload BLOB NULL
            )
            """);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO PrefixItems (Id, Name, Quantity, Payload)
                VALUES (@id, $name, :quantity, $payload)
                """;
            insert.Parameters.Add(new SqliteWasmParameter("id", 1));
            insert.Parameters.Add(new SqliteWasmParameter("name", "Alpha"));
            insert.Parameters.Add(new SqliteWasmParameter("quantity", 7));
            insert.Parameters.Add(new SqliteWasmParameter("payload", new byte[] { 4, 5, 6 }));

            var inserted = await insert.ExecuteNonQueryAsync();
            if (inserted != 1)
            {
                throw new InvalidOperationException($"Expected one inserted row, got {inserted}.");
            }
        }

        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT Name, Quantity, Payload
                FROM PrefixItems
                WHERE Id = $id
                  AND Name = :name
                  AND Quantity = @quantity
                """;
            query.Parameters.Add(new SqliteWasmParameter("id", 1));
            query.Parameters.Add(new SqliteWasmParameter("name", "Alpha"));
            query.Parameters.Add(new SqliteWasmParameter("quantity", 7));

            await using var reader = await query.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("Bare-name parameter aliases did not bind across prefixes.");
            }

            if (reader.GetString(0) != "Alpha" || reader.GetInt32(1) != 7)
            {
                throw new InvalidOperationException("Prefix compatibility query returned unexpected scalar values.");
            }

            var payload = new byte[3];
            var copied = reader.GetBytes(2, 0, payload, 0, payload.Length);
            if (copied != 3 || !payload.SequenceEqual(new byte[] { 4, 5, 6 }))
            {
                throw new InvalidOperationException("Bare-name blob parameter did not bind through the packed blob path.");
            }
        }

        await using (var collectionCommand = connection.CreateCommand())
        {
            var parameter = new SqliteWasmParameter("@lookup", 1);
            collectionCommand.Parameters.Add(parameter);

            if (!collectionCommand.Parameters.Contains("lookup") ||
                !collectionCommand.Parameters.Contains("$lookup") ||
                collectionCommand.Parameters.IndexOf(":lookup") != 0 ||
                collectionCommand.Parameters.Contains(new object()))
            {
                throw new InvalidOperationException("Parameter collection prefix lookup did not match native-style behavior.");
            }

            collectionCommand.Parameters.RemoveAt("lookup");
            if (collectionCommand.Parameters.Count != 0)
            {
                throw new InvalidOperationException("Parameter collection RemoveAt did not honor prefix-insensitive lookup.");
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
}
