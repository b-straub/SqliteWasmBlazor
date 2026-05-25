using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativePragmaIntrospectionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativePragmaIntrospection";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE pragma_parent (
                    Id INTEGER PRIMARY KEY
                );
                CREATE TABLE pragma_child (
                    Id INTEGER PRIMARY KEY,
                    ParentId INTEGER REFERENCES pragma_parent(Id),
                    Name TEXT GENERATED ALWAYS AS ('child-' || Id) VIRTUAL
                );
                CREATE INDEX idx_pragma_child_parent
                    ON pragma_child(ParentId)
                    WHERE ParentId IS NOT NULL;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(DISTINCT name) FROM pragma_function_list
                 WHERE name IN ('json_extract', 'timediff', 'bm25', 'octet_length', 'soundex', 'sha3')) AS FunctionCountValue,
                (SELECT count(*) FROM pragma_module_list
                 WHERE name IN ('fts5', 'fts5vocab')) AS ModuleCountValue,
                (SELECT count(*) FROM pragma_compile_options
                 WHERE compile_options = 'ENABLE_FTS5') AS FtsCompileOptionValue,
                (SELECT count(*) FROM pragma_table_info('pragma_child')) AS TableInfoColumnCountValue,
                (SELECT count(*) FROM pragma_table_xinfo('pragma_child')
                 WHERE name = 'Name' AND hidden <> 0) AS GeneratedColumnCountValue,
                (SELECT count(*) FROM pragma_foreign_key_list('pragma_child')
                 WHERE "table" = 'pragma_parent' AND "from" = 'ParentId' AND "to" = 'Id') AS ForeignKeyCountValue,
                (SELECT count(*) FROM pragma_index_list('pragma_child')
                 WHERE name = 'idx_pragma_child_parent' AND partial = 1) AS PartialIndexCountValue,
                (SELECT count(*) FROM pragma_database_list
                 WHERE name = 'main') AS MainDatabaseCountValue
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native PRAGMA introspection row.");
        }

        AssertEqual(6, reader.GetInt32(reader.GetOrdinal("FunctionCountValue")), "pragma_function_list");
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("ModuleCountValue")), "pragma_module_list");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("FtsCompileOptionValue")), "pragma_compile_options");
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("TableInfoColumnCountValue")), "pragma_table_info");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("GeneratedColumnCountValue")), "pragma_table_xinfo");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("ForeignKeyCountValue")), "pragma_foreign_key_list");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("PartialIndexCountValue")), "pragma_index_list");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("MainDatabaseCountValue")), "pragma_database_list");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native PRAGMA introspection row.");
        }

        return "OK";
    }

    private static void AssertEqual<T>(T expected, T actual, string pragmaName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"SQLite {pragmaName} returned {actual}; expected {expected}.");
        }
    }
}
