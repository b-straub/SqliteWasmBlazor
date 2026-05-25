using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativeStateFunctionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativeStateFunctions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE native_state_test (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL
                );
                INSERT INTO native_state_test (Name) VALUES ('alpha'), ('beta');
                UPDATE native_state_test SET Name = upper(Name) WHERE Id = 1;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                changes() AS ChangesValue,
                total_changes() AS TotalChangesValue,
                last_insert_rowid() AS LastInsertRowIdValue,
                typeof(sqlite_offset(Name)) AS OffsetTypeValue,
                sqlite_offset(Name) > 0 AS HasOffsetValue
            FROM native_state_test
            WHERE Id = 1
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native SQLite state function row.");
        }

        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("ChangesValue")), "changes");
        AssertEqual(3, reader.GetInt32(reader.GetOrdinal("TotalChangesValue")), "total_changes");
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("LastInsertRowIdValue")), "last_insert_rowid");
        AssertEqual("integer", reader.GetString(reader.GetOrdinal("OffsetTypeValue")), "sqlite_offset typeof");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("HasOffsetValue")), "sqlite_offset");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native SQLite state function row.");
        }

        return "OK";
    }

    private static void AssertEqual<T>(T expected, T actual, string functionName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned {actual}; expected {expected}.");
        }
    }
}
