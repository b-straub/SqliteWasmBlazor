using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class AsyncCommandRoundTripTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_AsyncCommandRoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = """
                CREATE TABLE AdoNetItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Quantity INTEGER NOT NULL,
                    Price REAL NOT NULL,
                    IsActive INTEGER NOT NULL,
                    Payload BLOB NULL,
                    Notes TEXT NULL
                )
                """;
            await createCommand.ExecuteNonQueryAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO AdoNetItems (Name, Quantity, Price, IsActive, Payload, Notes)
                VALUES (@name, @quantity, @price, @isActive, @payload, @notes)
                """;
            insertCommand.Parameters.Add(new SqliteWasmParameter("@name", "Alpha"));
            insertCommand.Parameters.Add(new SqliteWasmParameter("@quantity", 7));
            insertCommand.Parameters.Add(new SqliteWasmParameter("@price", 12.5m));
            insertCommand.Parameters.Add(new SqliteWasmParameter("@isActive", true));
            insertCommand.Parameters.Add(new SqliteWasmParameter("@payload", new byte[] { 1, 2, 3, 255 }));
            insertCommand.Parameters.Add(new SqliteWasmParameter("@notes", DBNull.Value));

            var inserted = await insertCommand.ExecuteNonQueryAsync();
            if (inserted != 1)
            {
                throw new InvalidOperationException($"Expected one inserted row, got {inserted}.");
            }

            await transaction.CommitAsync();
        }

        await using (var rollbackTransaction = await connection.BeginTransactionAsync())
        {
            await using var rollbackCommand = connection.CreateCommand();
            rollbackCommand.Transaction = rollbackTransaction;
            rollbackCommand.CommandText = """
                INSERT INTO AdoNetItems (Name, Quantity, Price, IsActive)
                VALUES (@name, @quantity, @price, @isActive)
                """;
            rollbackCommand.Parameters.Add(new SqliteWasmParameter("@name", "Rolled back"));
            rollbackCommand.Parameters.Add(new SqliteWasmParameter("@quantity", 1));
            rollbackCommand.Parameters.Add(new SqliteWasmParameter("@price", 2.0));
            rollbackCommand.Parameters.Add(new SqliteWasmParameter("@isActive", false));
            await rollbackCommand.ExecuteNonQueryAsync();
            await rollbackTransaction.RollbackAsync();
        }

        await using (var scalarCommand = connection.CreateCommand())
        {
            scalarCommand.CommandText = "SELECT COUNT(*) FROM AdoNetItems";
            var count = Convert.ToInt32(await scalarCommand.ExecuteScalarAsync());
            if (count != 1)
            {
                throw new InvalidOperationException($"Expected committed row count to be 1, got {count}.");
            }
        }

        await using (var queryCommand = connection.CreateCommand())
        {
            queryCommand.CommandText = """
                SELECT Name, Quantity, Price, IsActive, Payload, Notes
                FROM AdoNetItems
                WHERE Name = @name
                """;
            queryCommand.Parameters.Add(new SqliteWasmParameter("@name", "Alpha"));

            await using var reader = await queryCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("Expected one ADO.NET row.");
            }

            if (reader.GetString(0) != "Alpha" ||
                reader.GetInt32(1) != 7 ||
                Math.Abs(reader.GetDouble(2) - 12.5) > 0.0001 ||
                !reader.GetBoolean(3))
            {
                throw new InvalidOperationException("ADO.NET scalar values did not round-trip.");
            }

            var payload = new byte[4];
            var copied = reader.GetBytes(4, 0, payload, 0, payload.Length);
            if (copied != 4 || !payload.SequenceEqual(new byte[] { 1, 2, 3, 255 }))
            {
                throw new InvalidOperationException("ADO.NET blob value did not round-trip.");
            }

            if (!reader.IsDBNull(5))
            {
                throw new InvalidOperationException("ADO.NET null value did not round-trip.");
            }

            if (await reader.ReadAsync())
            {
                throw new InvalidOperationException("Expected only one ADO.NET row.");
            }
        }

        return "OK";
    }
}
