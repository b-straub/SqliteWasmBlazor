// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// Represents typed row data with SQLite column type metadata.
/// Used for efficient deserialization without JsonElement boxing.
/// </summary>
public sealed class TypedRowData
{
    /// <summary>
    /// SQLite column types (INTEGER, REAL, TEXT, BLOB).
    /// </summary>
    public string[] Types { get; set; } = [];

    /// <summary>
    /// Row data already deserialized to proper C# types (no JsonElement wrappers).
    /// </summary>
    public List<List<object?>> Data { get; set; } = [];
}

/// <summary>
/// Custom JSON converter that uses column type metadata to deserialize row values
/// directly to typed objects, eliminating JsonElement boxing overhead.
/// </summary>
public sealed class TypedRowDataConverter : JsonConverter<TypedRowData>
{
    public override TypedRowData Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new TypedRowData();

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object for TypedRowData");
        }

        reader.Read(); // {

        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name");
            }

            var propertyName = reader.GetString();
            reader.Read(); // Move to value

            if (propertyName == "types")
            {
                result.Types = ReadStringArray(ref reader);
            }
            else if (propertyName == "data")
            {
                result.Data = ReadTypedRows(ref reader, result.Types);
            }
            else
            {
                // Skip unknown properties
                reader.Skip();
            }

            reader.Read(); // Move to next property or end object
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, TypedRowData value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("types");
        writer.WriteStartArray();
        foreach (var type in value.Types)
        {
            writer.WriteStringValue(type);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("data");
        writer.WriteStartArray();
        foreach (var row in value.Data)
        {
            writer.WriteStartArray();
            foreach (var cell in row)
            {
                if (cell is null)
                {
                    writer.WriteNullValue();
                }
                else if (cell is string str)
                {
                    writer.WriteStringValue(str);
                }
                else if (cell is long l)
                {
                    writer.WriteNumberValue(l);
                }
                else if (cell is int i)
                {
                    writer.WriteNumberValue(i);
                }
                else if (cell is double d)
                {
                    writer.WriteNumberValue(d);
                }
                else if (cell is bool b)
                {
                    writer.WriteBooleanValue(b);
                }
                else if (cell is byte[] bytes)
                {
                    writer.WriteStringValue(Convert.ToBase64String(bytes));
                }
                else
                {
                    writer.WriteStringValue(cell.ToString() ?? string.Empty);
                }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static string[] ReadStringArray(ref Utf8JsonReader reader)
    {
        var list = new List<string>();

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected start of array for types");
        }

        reader.Read(); // [

        while (reader.TokenType != JsonTokenType.EndArray)
        {
            var value = reader.GetString();
            if (value is not null)
            {
                list.Add(value);
            }
            reader.Read();
        }

        return list.ToArray();
    }

    private static List<List<object?>> ReadTypedRows(ref Utf8JsonReader reader, string[] types)
    {
        var rows = new List<List<object?>>();

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected start of array for data");
        }

        reader.Read(); // [

        while (reader.TokenType != JsonTokenType.EndArray)
        {
            var row = ReadTypedRow(ref reader, types);
            rows.Add(row);
            reader.Read(); // Move to next row or end array
        }

        return rows;
    }

    private static List<object?> ReadTypedRow(ref Utf8JsonReader reader, string[] types)
    {
        var row = new List<object?>(types.Length);

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected start of array for row");
        }

        reader.Read(); // [

        for (int i = 0; i < types.Length && reader.TokenType != JsonTokenType.EndArray; i++)
        {
            var value = ReadTypedValue(ref reader, types[i]);
            row.Add(value);
            reader.Read(); // Move to next value or end array
        }

        return row;
    }

    private static object? ReadTypedValue(ref Utf8JsonReader reader, string sqliteType)
    {
        // Handle NULL values
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        // Deserialize based on SQLite column type
        return sqliteType switch
        {
            "INTEGER" => ReadInteger(ref reader),
            "REAL" => ReadReal(ref reader),
            "TEXT" => reader.GetString(),
            "BLOB" => ReadBlob(ref reader),
            _ => reader.GetString() // Unknown type, treat as TEXT
        };
    }

    private static object? ReadInteger(ref Utf8JsonReader reader)
    {
        // Handle both number tokens and string tokens (for BigInt > Number.MAX_SAFE_INTEGER)
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (str is not null && long.TryParse(str, out var longValue))
            {
                return longValue;
            }
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt64();
        }

        return null;
    }

    private static object? ReadReal(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDouble();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (str is not null && double.TryParse(str, out var doubleValue))
            {
                return doubleValue;
            }
        }

        return null;
    }

    private static object? ReadBlob(ref Utf8JsonReader reader)
    {
        // BLOB values are encoded as Base64 strings
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (str is not null)
            {
                try
                {
                    return Convert.FromBase64String(str);
                }
                catch
                {
                    // Invalid Base64, return null
                    return null;
                }
            }
        }

        return null;
    }
}
