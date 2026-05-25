using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativeFts5FunctionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativeFts5Functions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE VIRTUAL TABLE native_fts5_test USING fts5(Title, Body);
                INSERT INTO native_fts5_test (Title, Body)
                VALUES
                    ('First', 'alpha beta gamma'),
                    ('Second', 'beta delta');
                CREATE VIRTUAL TABLE native_fts5_vocab USING fts5vocab(native_fts5_test, 'row');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await VerifyAuxiliaryFunctionsAsync(connection);
        await VerifyVocabularyFunctionsAsync(connection);

        return "OK";
    }

    private static async Task VerifyAuxiliaryFunctionsAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                highlight(native_fts5_test, 1, '[', ']') AS HighlightValue,
                snippet(native_fts5_test, 1, '<b>', '</b>', '...', 3) AS SnippetValue,
                bm25(native_fts5_test) < 0 AS HasBm25RankValue,
                fts5_insttoken('prefix quer*') AS InstTokenValue
            FROM native_fts5_test
            WHERE native_fts5_test MATCH 'beta' AND rowid = 1
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native FTS5 auxiliary function row.");
        }

        AssertEqual("alpha [beta] gamma", reader.GetString(reader.GetOrdinal("HighlightValue")), "highlight");
        AssertEqual("alpha <b>beta</b> gamma", reader.GetString(reader.GetOrdinal("SnippetValue")), "snippet");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("HasBm25RankValue")), "bm25");
        AssertEqual("prefix quer*", reader.GetString(reader.GetOrdinal("InstTokenValue")), "fts5_insttoken");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native FTS5 auxiliary function row.");
        }
    }

    private static async Task VerifyVocabularyFunctionsAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                doc AS BetaDocumentCountValue,
                cnt AS BetaOccurrenceCountValue
            FROM native_fts5_vocab
            WHERE term = 'beta'
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native FTS5 vocabulary row.");
        }

        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("BetaDocumentCountValue")), "fts5vocab doc");
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("BetaOccurrenceCountValue")), "fts5vocab cnt");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native FTS5 vocabulary row.");
        }
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
