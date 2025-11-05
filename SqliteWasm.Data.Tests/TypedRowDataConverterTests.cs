// System.Data.SQLite.Wasm Tests
// MIT License

using System.Data.SQLite.Wasm;
using System.Text.Json;
using Xunit;

namespace SqliteWasm.Data.Tests;

public class TypedRowDataConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static TypedRowDataConverterTests()
    {
        Options.Converters.Add(new TypedRowDataConverter());
    }

    [Fact]
    public void DeserializeIntegerValues()
    {
        var json = """
        {
            "types": ["INTEGER", "INTEGER"],
            "data": [[1, 42], [100, 999]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Types.Length);
        Assert.Equal("INTEGER", result.Types[0]);
        Assert.Equal("INTEGER", result.Types[1]);

        Assert.Equal(2, result.Data.Count);
        Assert.Equal(2, result.Data[0].Count);
        Assert.Equal(1L, result.Data[0][0]);
        Assert.Equal(42L, result.Data[0][1]);
        Assert.Equal(100L, result.Data[1][0]);
        Assert.Equal(999L, result.Data[1][1]);
    }

    [Fact]
    public void DeserializeIntegerAsString_ForLargeValues()
    {
        // JavaScript sends large int64 as strings to preserve precision
        var json = """
        {
            "types": ["INTEGER"],
            "data": [["9223372036854775807"]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result.Data);
        Assert.Equal(9223372036854775807L, result.Data[0][0]);
    }

    [Fact]
    public void DeserializeRealValues()
    {
        var json = """
        {
            "types": ["REAL", "REAL"],
            "data": [[3.14, 2.71], [1.414, 1.732]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Data.Count);
        Assert.Equal(3.14, result.Data[0][0]);
        Assert.Equal(2.71, result.Data[0][1]);
        Assert.Equal(1.414, result.Data[1][0]);
        Assert.Equal(1.732, result.Data[1][1]);
    }

    [Fact]
    public void DeserializeTextValues()
    {
        var json = """
        {
            "types": ["TEXT", "TEXT"],
            "data": [["hello", "world"], ["foo", "bar"]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Data.Count);
        Assert.Equal("hello", result.Data[0][0]);
        Assert.Equal("world", result.Data[0][1]);
        Assert.Equal("foo", result.Data[1][0]);
        Assert.Equal("bar", result.Data[1][1]);
    }

    [Fact]
    public void DeserializeBlobValues()
    {
        // BLOB values are Base64 encoded
        var testBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var base64 = Convert.ToBase64String(testBytes);

        var json = $$"""
        {
            "types": ["BLOB"],
            "data": [["{{base64}}"]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result.Data);
        var bytes = Assert.IsType<byte[]>(result.Data[0][0]);
        Assert.Equal(testBytes, bytes);
    }

    [Fact]
    public void DeserializeNullValues()
    {
        var json = """
        {
            "types": ["INTEGER", "TEXT", "REAL", "BLOB"],
            "data": [[null, null, null, null]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result.Data);
        Assert.Null(result.Data[0][0]);
        Assert.Null(result.Data[0][1]);
        Assert.Null(result.Data[0][2]);
        Assert.Null(result.Data[0][3]);
    }

    [Fact]
    public void DeserializeMixedTypes()
    {
        var testBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var base64 = Convert.ToBase64String(testBytes);

        var json = $$"""
        {
            "types": ["INTEGER", "TEXT", "REAL", "BLOB"],
            "data": [[42, "test", 3.14, "{{base64}}"]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result.Data);
        Assert.Equal(42L, result.Data[0][0]);
        Assert.Equal("test", result.Data[0][1]);
        Assert.Equal(3.14, result.Data[0][2]);
        var bytes = Assert.IsType<byte[]>(result.Data[0][3]);
        Assert.Equal(testBytes, bytes);
    }

    [Fact]
    public void DeserializeMultipleRows()
    {
        var json = """
        {
            "types": ["INTEGER", "TEXT"],
            "data": [
                [1, "first"],
                [2, "second"],
                [3, "third"]
            ]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(3, result.Data.Count);
        Assert.Equal(1L, result.Data[0][0]);
        Assert.Equal("first", result.Data[0][1]);
        Assert.Equal(2L, result.Data[1][0]);
        Assert.Equal("second", result.Data[1][1]);
        Assert.Equal(3L, result.Data[2][0]);
        Assert.Equal("third", result.Data[2][1]);
    }

    [Fact]
    public void DeserializeEmptyData()
    {
        var json = """
        {
            "types": ["INTEGER", "TEXT"],
            "data": []
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.Types.Length);
        Assert.Empty(result.Data);
    }

    [Fact]
    public void SerializeAndDeserializeRoundTrip()
    {
        var original = new TypedRowData
        {
            Types = new[] { "INTEGER", "TEXT", "REAL" },
            Data = new List<List<object?>>
            {
                new() { 1L, "test", 3.14 },
                new() { 2L, "hello", 2.71 }
            }
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Types, deserialized.Types);
        Assert.Equal(original.Data.Count, deserialized.Data.Count);
        Assert.Equal(original.Data[0][0], deserialized.Data[0][0]);
        Assert.Equal(original.Data[0][1], deserialized.Data[0][1]);
        Assert.Equal(original.Data[0][2], deserialized.Data[0][2]);
    }

    [Fact]
    public void DeserializeUnknownTypeDefaultsToText()
    {
        var json = """
        {
            "types": ["UNKNOWN"],
            "data": [["value"]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result.Data);
        Assert.Equal("value", result.Data[0][0]);
    }

    [Fact]
    public void DeserializeInvalidBase64ReturnsNull()
    {
        var json = """
        {
            "types": ["BLOB"],
            "data": [["not-valid-base64!@#$"]]
        }
        """;

        var result = JsonSerializer.Deserialize<TypedRowData>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result.Data);
        Assert.Null(result.Data[0][0]);
    }
}
