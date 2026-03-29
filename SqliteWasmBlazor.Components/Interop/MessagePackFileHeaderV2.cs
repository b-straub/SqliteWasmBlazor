using System.Reflection;
using MessagePack;

namespace SqliteWasmBlazor.Components.Interop;

/// <summary>
/// V2 self-describing header for worker-side bulk import/export.
/// Contains column metadata so the worker can autonomously build SQL and handle type conversions.
/// </summary>
[MessagePackObject]
public class MessagePackFileHeaderV2
{
    [Key(0)]
    public string MagicNumber { get; set; } = "SWBV2";

    [Key(1)]
    public string SchemaHash { get; set; } = string.Empty;

    [Key(2)]
    public string DataType { get; set; } = string.Empty;

    [Key(3)]
    public string? AppIdentifier { get; set; }

    /// <summary>
    /// ISO 8601 string (not DateTime — avoids Timestamp ext for header portability)
    /// </summary>
    [Key(4)]
    public string ExportedAt { get; set; } = string.Empty;

    [Key(5)]
    public int RecordCount { get; set; }

    /// <summary>
    /// 0 = Seed (full data, plain INSERT), 1 = Delta (changed data, supports UPSERT)
    /// </summary>
    [Key(6)]
    public int Mode { get; set; }

    [Key(7)]
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// Column metadata: each entry is [columnName, sqlType, csharpType]
    /// Order matches the [Key(n)] positions in the serialized items.
    /// </summary>
    [Key(8)]
    public string[][] Columns { get; set; } = [];

    /// <summary>
    /// Primary key column name for ON CONFLICT clauses
    /// </summary>
    [Key(9)]
    public string PrimaryKeyColumn { get; set; } = string.Empty;

    public void Validate(string expectedType, string? expectedSchemaHash = null, string? expectedAppId = null)
    {
        if (MagicNumber != "SWBV2")
        {
            throw new InvalidOperationException(
                $"Invalid file format: expected V2 MessagePack export file (magic 'SWBV2'), got '{MagicNumber}'");
        }

        if (DataType != expectedType)
        {
            throw new InvalidOperationException(
                $"Incompatible data type: expected '{expectedType}', file contains '{DataType}'");
        }

        if (expectedSchemaHash is not null && SchemaHash != expectedSchemaHash)
        {
            throw new InvalidOperationException(
                $"Incompatible schema: export hash '{SchemaHash}' does not match current '{expectedSchemaHash}'");
        }

        if (expectedAppId is not null && AppIdentifier != expectedAppId)
        {
            throw new InvalidOperationException(
                $"Incompatible application: expected '{expectedAppId}', file is from '{AppIdentifier}'");
        }

        if (RecordCount < 0)
        {
            throw new InvalidOperationException($"Invalid record count: {RecordCount}");
        }

        if (Columns.Length == 0)
        {
            throw new InvalidOperationException("Header contains no column metadata");
        }

        if (string.IsNullOrEmpty(TableName))
        {
            throw new InvalidOperationException("Header contains no table name");
        }

        if (string.IsNullOrEmpty(PrimaryKeyColumn))
        {
            throw new InvalidOperationException("Header contains no primary key column");
        }
    }

    /// <summary>
    /// Create a V2 header from a MessagePack DTO type.
    /// Reflects [Key(n)] attributes to build column metadata.
    /// Column names are assumed to match the SQL column names (DTO property names = entity property names).
    /// </summary>
    /// <param name="tableName">SQL table name</param>
    /// <param name="primaryKeyColumn">Primary key column name for ON CONFLICT</param>
    /// <param name="recordCount">Total record count</param>
    /// <param name="mode">0 = Seed, 1 = Delta</param>
    /// <param name="appIdentifier">Optional app identifier</param>
    public static MessagePackFileHeaderV2 Create<T>(
        string tableName,
        string primaryKeyColumn,
        int recordCount,
        int mode = 0,
        string? appIdentifier = null)
    {
        var columns = BuildColumnMetadata(typeof(T));

        return new MessagePackFileHeaderV2
        {
            MagicNumber = "SWBV2",
            SchemaHash = SchemaHashGenerator.ComputeHash<T>(),
            DataType = typeof(T).FullName ?? typeof(T).Name,
            AppIdentifier = appIdentifier,
            ExportedAt = DateTime.UtcNow.ToString("O"),
            RecordCount = recordCount,
            Mode = mode,
            TableName = tableName,
            Columns = columns,
            PrimaryKeyColumn = primaryKeyColumn
        };
    }

    /// <summary>
    /// Reflect [Key(n)] attributes to build column metadata array.
    /// Each entry: [propertyName, sqlType, csharpTypeName]
    /// </summary>
    private static string[][] BuildColumnMetadata(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new
            {
                Property = p,
                KeyAttr = p.GetCustomAttribute<KeyAttribute>()
            })
            .Where(x => x.KeyAttr is not null)
            .OrderBy(x => x.KeyAttr!.IntKey)
            .ToList();

        if (properties.Count == 0)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' has no properties with [Key] attributes.");
        }

        return properties.Select(p =>
        {
            var propType = Nullable.GetUnderlyingType(p.Property.PropertyType) ?? p.Property.PropertyType;
            var isNullable = Nullable.GetUnderlyingType(p.Property.PropertyType) is not null
                || !p.Property.PropertyType.IsValueType;

            var sqlType = GetSqlType(propType);
            var csharpType = GetCsharpTypeName(propType, isNullable);

            return new[] { p.Property.Name, sqlType, csharpType };
        }).ToArray();
    }

    private static string GetSqlType(Type type)
    {
        if (type == typeof(Guid))
        {
            return "BLOB";
        }

        if (type == typeof(string))
        {
            return "TEXT";
        }

        if (type == typeof(bool))
        {
            return "INTEGER";
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short)
            || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong)
            || type == typeof(ushort) || type == typeof(sbyte))
        {
            return "INTEGER";
        }

        if (type == typeof(TimeSpan))
        {
            return "INTEGER"; // Stored as Ticks (int64)
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "TEXT"; // Stored as ISO 8601
        }

        if (type == typeof(decimal))
        {
            return "TEXT"; // String representation to avoid precision loss
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return "REAL";
        }

        if (type == typeof(byte[]))
        {
            return "BLOB";
        }

        if (type == typeof(char))
        {
            return "TEXT";
        }

        if (type.IsEnum)
        {
            return "INTEGER";
        }

        return "TEXT";
    }

    private static string GetCsharpTypeName(Type type, bool isNullable)
    {
        // Handle enums — serialized as underlying integer by MessagePack
        if (type.IsEnum)
        {
            var name = "Enum";
            return isNullable ? $"{name}?" : name;
        }

        var typeName = type == typeof(byte[]) ? "ByteArray" : type.Name;

        var mapped = typeName switch
        {
            "Guid" => "Guid",
            "String" => "String",
            "Boolean" => "Boolean",
            "DateTime" => "DateTime",
            "DateTimeOffset" => "DateTimeOffset",
            "TimeSpan" => "TimeSpan",
            "Int32" => "Int32",
            "Int64" => "Int64",
            "Int16" => "Int16",
            "Byte" => "Byte",
            "UInt16" => "UInt16",
            "UInt32" => "UInt32",
            "UInt64" => "UInt64",
            "Double" => "Double",
            "Single" => "Single",
            "Decimal" => "Decimal",
            "Char" => "Char",
            "ByteArray" => "ByteArray",
            _ => typeName
        };

        return isNullable ? $"{mapped}?" : mapped;
    }
}
