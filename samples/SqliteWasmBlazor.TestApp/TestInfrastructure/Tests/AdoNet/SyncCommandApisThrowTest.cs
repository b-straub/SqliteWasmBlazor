using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class SyncCommandApisThrowTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_SyncCommandApisThrow";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        AssertNotSupported(() => command.ExecuteNonQuery(), "ExecuteNonQueryAsync");
        AssertNotSupported(() => command.ExecuteScalar(), "ExecuteScalarAsync");
        AssertNotSupported(() => command.ExecuteReader().Dispose(), "ExecuteReaderAsync");

        return "OK";
    }

    private static void AssertNotSupported(Action action, string expectedAsyncMethod)
    {
        try
        {
            action();
        }
        catch (NotSupportedException ex) when (ex.Message.Contains(expectedAsyncMethod, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"Expected NotSupportedException mentioning {expectedAsyncMethod}.");
    }
}
