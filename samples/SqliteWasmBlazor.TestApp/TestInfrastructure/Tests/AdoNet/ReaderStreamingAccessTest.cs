using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ReaderStreamingAccessTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ReaderStreamingAccess";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = """
                CREATE TABLE ReaderStreamingItems (
                    Id INTEGER PRIMARY KEY,
                    Payload BLOB NOT NULL,
                    TextValue TEXT NOT NULL
                )
                """;
            await createCommand.ExecuteNonQueryAsync();
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText = """
                INSERT INTO ReaderStreamingItems (Id, Payload, TextValue)
                VALUES (1, @payload, @textValue)
                """;
            insertCommand.Parameters.Add(new SqliteWasmParameter("@payload", new byte[] { 10, 20, 30, 40, 50 }));
            insertCommand.Parameters.Add(new SqliteWasmParameter("@textValue", "abcdef"));
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = "SELECT Payload, TextValue FROM ReaderStreamingItems WHERE Id = 1";

        await using var reader = await queryCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one streaming test row.");
        }

        if (reader.GetBytes(0, 0, null, 0, 0) != 5 ||
            reader.GetChars(1, 0, null, 0, 0) != 6)
        {
            throw new InvalidOperationException("GetBytes/GetChars did not return full lengths for null buffers.");
        }

        var bytes = new byte[3];
        var byteCount = reader.GetBytes(0, 1, bytes, 0, bytes.Length);
        if (byteCount != 3 || !bytes.SequenceEqual(new byte[] { 20, 30, 40 }))
        {
            throw new InvalidOperationException("GetBytes chunk read returned unexpected data.");
        }

        var emptyByteCount = reader.GetBytes(0, 5, bytes, 0, bytes.Length);
        if (emptyByteCount != 0)
        {
            throw new InvalidOperationException("GetBytes at end of value did not return 0.");
        }

        var chars = new char[3];
        var charCount = reader.GetChars(1, 2, chars, 0, chars.Length);
        if (charCount != 3 || !chars.SequenceEqual(['c', 'd', 'e']))
        {
            var fullText = reader.GetString(1);
            throw new InvalidOperationException(
                $"GetChars chunk read returned unexpected data: count={charCount}, " +
                $"charCodes=[{string.Join(",", chars.Select(c => (int)c))}], " +
                $"fullText='{fullText}', fullTextCodes=[{string.Join(",", fullText.Select(c => (int)c))}].");
        }

        var emptyCharCount = reader.GetChars(1, 6, chars, 0, chars.Length);
        if (emptyCharCount != 0)
        {
            throw new InvalidOperationException("GetChars at end of value did not return 0.");
        }

        await using (var stream = reader.GetStream(0))
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            if (!memory.ToArray().SequenceEqual(new byte[] { 10, 20, 30, 40, 50 }))
            {
                throw new InvalidOperationException("GetStream returned unexpected blob data.");
            }
        }

        using (var textReader = reader.GetTextReader(1))
        {
            var text = await textReader.ReadToEndAsync();
            if (text != "abcdef")
            {
                throw new InvalidOperationException($"GetTextReader returned unexpected text: '{text}'.");
            }
        }

        await using (var genericStream = reader.GetFieldValue<Stream>(0))
        {
            var first = genericStream.ReadByte();
            if (first != 10)
            {
                throw new InvalidOperationException($"GetFieldValue<Stream> returned unexpected first byte: {first}.");
            }
        }

        using (var genericTextReader = reader.GetFieldValue<TextReader>(1))
        {
            var firstThree = new char[3];
            var read = await genericTextReader.ReadAsync(firstThree, 0, firstThree.Length);
            if (read != 3 || !firstThree.SequenceEqual(['a', 'b', 'c']))
            {
                throw new InvalidOperationException("GetFieldValue<TextReader> returned unexpected text.");
            }
        }

        try
        {
            reader.GetChars(1, 7, chars, 0, chars.Length);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "OK";
        }

        throw new InvalidOperationException("GetChars beyond the end of the value did not throw.");
    }
}
