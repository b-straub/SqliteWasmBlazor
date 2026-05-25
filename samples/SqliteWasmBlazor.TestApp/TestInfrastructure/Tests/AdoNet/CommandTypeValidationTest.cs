using System.Data;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class CommandTypeValidationTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_CommandTypeValidation";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await AssertUnsupportedCommandTypeAsync(connection, CommandType.StoredProcedure);
        await AssertUnsupportedCommandTypeAsync(connection, CommandType.TableDirect);

        await using var textCommand = connection.CreateCommand();
        textCommand.CommandType = CommandType.Text;
        textCommand.CommandText = "SELECT 42";

        var result = Convert.ToInt32(await textCommand.ExecuteScalarAsync());
        if (result != 42)
        {
            throw new InvalidOperationException($"CommandType.Text returned unexpected scalar result {result}.");
        }

        return "OK";
    }

    private static async Task AssertUnsupportedCommandTypeAsync(
        System.Data.Common.DbConnection connection,
        CommandType commandType)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = commandType;
        command.CommandText = "SELECT 1";

        try
        {
            await command.ExecuteScalarAsync();
        }
        catch (NotSupportedException ex) when (
            ex.Message.Contains(nameof(CommandType.Text), StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"Expected {commandType} to be rejected.");
    }
}
