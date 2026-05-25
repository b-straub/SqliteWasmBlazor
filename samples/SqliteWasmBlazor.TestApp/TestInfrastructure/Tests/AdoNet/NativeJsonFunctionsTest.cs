using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativeJsonFunctionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativeJsonFunctions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await VerifyJsonScalarFunctionsAsync(connection);
        await VerifyJsonTableValuedFunctionsAsync(connection);

        return "OK";
    }

    private static async Task VerifyJsonScalarFunctionsAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                json('{"b":2,"a":1}') AS JsonValue,
                json_array(1, 'two', NULL) AS JsonArrayValue,
                json_array_length('[1,2,3]') AS JsonArrayLengthValue,
                json_error_position('{"bad":') AS JsonErrorPositionValue,
                json_extract('{"items":[{"name":"alpha","qty":2}]}', '$.items[0].qty') AS JsonExtractValue,
                json_insert('{"a":1}', '$.b', 2) AS JsonInsertValue,
                json_object('name', 'alpha', 'qty', 2) AS JsonObjectValue,
                json_patch('{"a":1,"b":2}', '{"b":9,"c":3}') AS JsonPatchValue,
                json_remove('{"a":1,"b":2}', '$.b') AS JsonRemoveValue,
                json_replace('{"a":1,"b":2}', '$.b', 9) AS JsonReplaceValue,
                json_set('{"a":1}', '$.a', 9, '$.b', 2) AS JsonSetValue,
                json_type('{"a":[1]}', '$.a') AS JsonTypeValue,
                json_valid('{"a":1}') AS JsonValidValue,
                json_quote('alpha') AS JsonQuoteValue,
                '{"a":{"b":7}}' -> '$.a' AS ArrowJsonValue,
                '{"a":{"b":7}}' ->> '$.a.b' AS ArrowTextValue,
                typeof(jsonb('{"a":1}')) AS JsonbStorageClassValue,
                json_extract(jsonb('{"a":{"b":7}}'), '$.a.b') AS JsonbExtractValue
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native JSON function row.");
        }

        AssertJsonObjectContains(reader.GetString(reader.GetOrdinal("JsonValue")), "a", 1, "json");
        AssertEqual("[1,\"two\",null]", reader.GetString(reader.GetOrdinal("JsonArrayValue")), "json_array");
        AssertEqual(3, reader.GetInt32(reader.GetOrdinal("JsonArrayLengthValue")), "json_array_length");
        if (reader.GetInt32(reader.GetOrdinal("JsonErrorPositionValue")) <= 0)
        {
            throw new InvalidOperationException("SQLite function json_error_position returned a non-positive offset.");
        }
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("JsonExtractValue")), "json_extract");
        AssertJsonObjectContains(reader.GetString(reader.GetOrdinal("JsonInsertValue")), "b", 2, "json_insert");
        AssertJsonObjectContains(reader.GetString(reader.GetOrdinal("JsonObjectValue")), "qty", 2, "json_object");
        AssertJsonObjectContains(reader.GetString(reader.GetOrdinal("JsonPatchValue")), "b", 9, "json_patch");
        AssertJsonObjectDoesNotContain(reader.GetString(reader.GetOrdinal("JsonRemoveValue")), "b", "json_remove");
        AssertJsonObjectContains(reader.GetString(reader.GetOrdinal("JsonReplaceValue")), "b", 9, "json_replace");
        AssertJsonObjectContains(reader.GetString(reader.GetOrdinal("JsonSetValue")), "b", 2, "json_set");
        AssertEqual("array", reader.GetString(reader.GetOrdinal("JsonTypeValue")), "json_type");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("JsonValidValue")), "json_valid");
        AssertEqual("\"alpha\"", reader.GetString(reader.GetOrdinal("JsonQuoteValue")), "json_quote");
        AssertJsonObjectContains(reader.GetString(reader.GetOrdinal("ArrowJsonValue")), "b", 7, "->");
        AssertEqual(7, reader.GetInt32(reader.GetOrdinal("ArrowTextValue")), "->>");
        AssertEqual("blob", reader.GetString(reader.GetOrdinal("JsonbStorageClassValue")), "jsonb");
        AssertEqual(7, reader.GetInt32(reader.GetOrdinal("JsonbExtractValue")), "jsonb_extract");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native JSON function row.");
        }
    }

    private static async Task VerifyJsonTableValuedFunctionsAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH payload(value) AS (
                VALUES ('{"items":[{"name":"alpha","qty":2},{"name":"beta","qty":3}],"meta":{"open":true}}')
            )
            SELECT
                (
                    SELECT group_concat(json_extract(each.value, '$.name'), '|')
                    FROM payload, json_each(payload.value, '$.items') AS each
                ) AS EachNamesValue,
                (
                    SELECT sum(json_extract(each.value, '$.qty'))
                    FROM payload, json_each(payload.value, '$.items') AS each
                ) AS EachQtyValue,
                (
                    SELECT count(*)
                    FROM payload, json_tree(payload.value) AS tree
                    WHERE tree.key = 'open'
                ) AS TreeOpenKeyCountValue
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native JSON table-valued function row.");
        }

        AssertEqual("alpha|beta", reader.GetString(reader.GetOrdinal("EachNamesValue")), "json_each");
        AssertEqual(5, reader.GetInt32(reader.GetOrdinal("EachQtyValue")), "json_each aggregate");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("TreeOpenKeyCountValue")), "json_tree");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native JSON table-valued function row.");
        }
    }

    private static void AssertJsonObjectContains(string value, string propertyName, int expected, string functionName)
    {
        using var document = System.Text.Json.JsonDocument.Parse(value);
        if (!document.RootElement.TryGetProperty(propertyName, out var property) ||
            property.GetInt32() != expected)
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned {value}; expected {propertyName}={expected}.");
        }
    }

    private static void AssertJsonObjectDoesNotContain(string value, string propertyName, string functionName)
    {
        using var document = System.Text.Json.JsonDocument.Parse(value);
        if (document.RootElement.TryGetProperty(propertyName, out _))
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned {value}; expected no {propertyName} property.");
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
