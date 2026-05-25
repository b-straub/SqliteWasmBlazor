using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ReaderRecordsAffectedTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ReaderRecordsAffected";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = """
                /* leading comment */
                SELECT 1 AS Value
                """;

            await using var reader = await selectCommand.ExecuteReaderAsync();
            if (reader.RecordsAffected != -1)
            {
                throw new InvalidOperationException(
                    $"SELECT reader RecordsAffected should be -1, got {reader.RecordsAffected}.");
            }

            if (!await reader.ReadAsync() || reader.GetInt32(0) != 1)
            {
                throw new InvalidOperationException("SELECT reader did not return the expected row.");
            }
        }

        await using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA foreign_keys";

            await using var reader = await pragmaCommand.ExecuteReaderAsync();
            if (reader.RecordsAffected != -1)
            {
                throw new InvalidOperationException(
                    $"PRAGMA reader RecordsAffected should be -1, got {reader.RecordsAffected}.");
            }
        }

        return "OK";
    }
}
