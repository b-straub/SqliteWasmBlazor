using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ConnectionStringParsingTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ConnectionStringParsing";

    public override ValueTask<string?> RunTestAsync()
    {
        AssertDatabaseName("Data Source=primary.db", "primary.db");
        AssertDatabaseName("DataSource=compact.db", "compact.db");
        AssertDatabaseName("Filename=file-name.db", "file-name.db");
        AssertDatabaseName("Cache=Shared;Data Source='quoted;name.db';Mode=ReadWriteCreate", "quoted;name.db");
        AssertDatabaseName("Mode=ReadWriteCreate;Filename=\"quoted filename.db\";Cache=Shared", "quoted filename.db");
        AssertDatabaseName("Mode=Memory", ":memory:");

        return ValueTask.FromResult<string?>("OK");
    }

    private static void AssertDatabaseName(string connectionString, string expected)
    {
        using var connection = new SqliteWasmConnection(connectionString);
        if (connection.Database != expected || connection.DataSource != expected)
        {
            throw new InvalidOperationException(
                $"Connection string '{connectionString}' parsed as '{connection.Database}', expected '{expected}'.");
        }
    }
}
