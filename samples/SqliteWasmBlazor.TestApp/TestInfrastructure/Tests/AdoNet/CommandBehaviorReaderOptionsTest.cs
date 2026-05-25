using System.Data;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class CommandBehaviorReaderOptionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_CommandBehaviorReaderOptions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE CommandBehaviorItems (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL
            )
            """);
        await ExecuteAsync(connection, """
            INSERT INTO CommandBehaviorItems (Id, Name)
            VALUES (1, 'Alpha'), (2, 'Bravo')
            """);

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = "SELECT Id, Name FROM CommandBehaviorItems ORDER BY Id";
            await using var reader = await schemaCommand.ExecuteReaderAsync(CommandBehavior.SchemaOnly);

            if (reader.FieldCount != 2 ||
                reader.GetName(0) != "Id" ||
                reader.GetFieldType(0) != typeof(long) ||
                reader.HasRows ||
                await reader.ReadAsync())
            {
                throw new InvalidOperationException("CommandBehavior.SchemaOnly did not expose metadata without rows.");
            }
        }

        await using (var singleRowCommand = connection.CreateCommand())
        {
            singleRowCommand.CommandText = "SELECT Id, Name FROM CommandBehaviorItems ORDER BY Id";
            await using var reader = await singleRowCommand.ExecuteReaderAsync(CommandBehavior.SingleRow);

            if (!await reader.ReadAsync() ||
                reader.GetInt32(0) != 1 ||
                reader.GetString(1) != "Alpha" ||
                await reader.ReadAsync())
            {
                throw new InvalidOperationException("CommandBehavior.SingleRow did not stop after the first row.");
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
