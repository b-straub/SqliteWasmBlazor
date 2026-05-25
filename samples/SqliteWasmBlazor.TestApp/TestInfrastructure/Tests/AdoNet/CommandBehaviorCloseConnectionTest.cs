using System.Data;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class CommandBehaviorCloseConnectionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_CommandBehaviorCloseConnection";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 AS Value";

        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection))
        {
            if (!await reader.ReadAsync() || reader.GetInt32(0) != 1)
            {
                throw new InvalidOperationException("CloseConnection reader did not return the expected row.");
            }
        }

        if (connection.State != ConnectionState.Closed)
        {
            throw new InvalidOperationException(
                $"CommandBehavior.CloseConnection did not close the connection. State: {connection.State}.");
        }

        await connection.OpenAsync();
        await using (var reopenCommand = connection.CreateCommand())
        {
            reopenCommand.CommandText = "SELECT 2";
            var value = Convert.ToInt32(await reopenCommand.ExecuteScalarAsync());
            if (value != 2)
            {
                throw new InvalidOperationException("Connection did not reopen after CloseConnection reader disposal.");
            }
        }

        return "OK";
    }
}
