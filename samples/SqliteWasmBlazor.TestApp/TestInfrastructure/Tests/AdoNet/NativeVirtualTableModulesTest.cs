using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativeVirtualTableModulesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativeVirtualTableModules";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE dbstat_probe (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL
                );
                INSERT INTO dbstat_probe (Name) VALUES ('alpha'), ('beta');

                CREATE VIRTUAL TABLE rtree_probe
                USING rtree(Id, MinX, MaxX, MinY, MaxY);
                INSERT INTO rtree_probe
                VALUES
                    (1, 0, 10, 0, 10),
                    (2, 20, 30, 20, 30);

                CREATE VIRTUAL TABLE rtree_i32_probe
                USING rtree_i32(Id, MinX, MaxX, MinY, MaxY);
                INSERT INTO rtree_i32_probe
                VALUES
                    (1, 0, 10, 0, 10),
                    (2, 20, 30, 20, 30);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM pragma_module_list
                 WHERE name IN (
                    'rtree',
                    'rtree_i32',
                    'dbstat',
                    'bytecode',
                    'sqlite_stmt',
                    'sqlite_dbpage',
                    'tables_used'
                 )) AS ModuleCountValue,
                (SELECT count(*) FROM pragma_compile_options
                 WHERE compile_options IN ('ENABLE_RTREE', 'ENABLE_DBSTAT_VTAB')) AS CompileOptionCountValue,
                (SELECT count(*) FROM rtree_probe
                 WHERE MinX <= 5 AND MaxX >= 5 AND MinY <= 5 AND MaxY >= 5) AS RTreeHitCountValue,
                (SELECT count(*) FROM rtree_i32_probe
                 WHERE MinX <= 5 AND MaxX >= 5 AND MinY <= 5 AND MaxY >= 5) AS RTreeI32HitCountValue,
                (SELECT count(*) FROM dbstat
                 WHERE name = 'dbstat_probe') AS DbstatPageCountValue,
                (SELECT count(*) FROM bytecode('SELECT Id, Name FROM dbstat_probe WHERE Id = ?')
                 WHERE opcode IN ('OpenRead', 'Variable')) AS BytecodeOpcodeCountValue,
                (SELECT count(*) FROM tables_used('SELECT Id FROM dbstat_probe')
                 WHERE type = 'table' AND schema = 'main' AND name = 'dbstat_probe') AS TablesUsedCountValue,
                (SELECT count(*) FROM sqlite_stmt
                 WHERE sql LIKE '%sqlite_stmt%' AND busy = 1) AS BusyStatementCountValue,
                (SELECT count(*) FROM sqlite_dbpage
                 WHERE pgno > 0 AND length(data) > 0) AS DbpageCountValue
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native virtual table module row.");
        }

        AssertEqual(7, reader.GetInt32(reader.GetOrdinal("ModuleCountValue")), "pragma_module_list");
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("CompileOptionCountValue")), "pragma_compile_options");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("RTreeHitCountValue")), "rtree");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("RTreeI32HitCountValue")), "rtree_i32");
        if (reader.GetInt32(reader.GetOrdinal("DbstatPageCountValue")) <= 0)
        {
            throw new InvalidOperationException("dbstat returned no rows for dbstat_probe.");
        }
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("BytecodeOpcodeCountValue")), "bytecode");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("TablesUsedCountValue")), "tables_used");
        if (reader.GetInt32(reader.GetOrdinal("BusyStatementCountValue")) <= 0)
        {
            throw new InvalidOperationException("sqlite_stmt returned no busy row for the active statement.");
        }
        if (reader.GetInt32(reader.GetOrdinal("DbpageCountValue")) <= 0)
        {
            throw new InvalidOperationException("sqlite_dbpage returned no readable database pages.");
        }

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native virtual table module row.");
        }

        return "OK";
    }

    private static void AssertEqual<T>(T expected, T actual, string moduleName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"SQLite module {moduleName} returned {actual}; expected {expected}.");
        }
    }
}
