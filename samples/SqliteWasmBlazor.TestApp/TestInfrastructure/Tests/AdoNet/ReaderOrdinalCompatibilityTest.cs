using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ReaderOrdinalCompatibilityTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ReaderOrdinalCompatibility";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1 AS MixedCase, 2 AS OtherColumn";

            await using var reader = await command.ExecuteReaderAsync();
            AssertEqual(0, reader.GetOrdinal("MixedCase"), "exact name ordinal");
            AssertEqual(0, reader.GetOrdinal("mixedcase"), "case-insensitive name ordinal");
            AssertEqual(1, reader.GetOrdinal("OTHERCOLUMN"), "upper-case name ordinal");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1 AS Name, 2 AS name";

            await using var reader = await command.ExecuteReaderAsync();
            AssertEqual(0, reader.GetOrdinal("Name"), "ambiguous exact first column");
            AssertEqual(1, reader.GetOrdinal("name"), "ambiguous exact second column");

            try
            {
                reader.GetOrdinal("NAME");
            }
            catch (InvalidOperationException)
            {
                return "OK";
            }

            throw new InvalidOperationException("Case-insensitive ambiguous column lookup did not throw.");
        }
    }

    private static void AssertEqual(int expected, int actual, string operation)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected {expected}, got {actual}.");
        }
    }
}
