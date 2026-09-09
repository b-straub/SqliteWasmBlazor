using System.Reflection;
using MessagePack;

namespace SqliteWasmBlazor;

/// <summary>
/// V2 self-describing header for worker-side bulk import/export.
/// Contains column metadata so the worker can autonomously build SQL and handle type conversions.
/// </summary>
[MessagePackObject]
public class MessagePackFileHeaderV2
{
    /// <summary>Format marker; always <c>SWBV2</c>. Checked by <see cref="Validate"/>.</summary>
    [Key(0)]
    public string MagicNumber { get; set; } = "SWBV2";

    /// <summary>Hash of the DTO layout the file was written from, from <see cref="SchemaHashGenerator"/>.</summary>
    [Key(1)]
    public string SchemaHash { get; set; } = string.Empty;

    /// <summary>The DTO type name the rows are, so an import can refuse the wrong file.</summary>
    [Key(2)]
    public string DataType { get; set; } = string.Empty;

    /// <summary>Optional writing-application marker; <c>null</c> when the writer set none.</summary>
    [Key(3)]
    public string? AppIdentifier { get; set; }

    /// <summary>
    /// ISO 8601 string (not DateTime — avoids Timestamp ext for header portability)
    /// </summary>
    [Key(4)]
    public string ExportedAt { get; set; } = string.Empty;

    /// <summary>How many rows follow the header.</summary>
    [Key(5)]
    public int RecordCount { get; set; }

    /// <summary>
    /// 0 = Seed (full data, plain INSERT), 1 = Delta (changed data, supports UPSERT)
    /// </summary>
    [Key(6)]
    public int Mode { get; set; }

    /// <summary>The SQL table the rows belong in.</summary>
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

    /// <summary>
    /// Throws unless the header is well-formed and matches what the caller
    /// expects. The import path calls this before writing anything.
    /// </summary>
    /// <param name="expectedType">The DTO type name the caller is importing.</param>
    /// <param name="expectedSchemaHash">The caller's current layout hash, or <c>null</c> to skip the check.</param>
    /// <param name="expectedAppId">The application marker to require, or <c>null</c> to skip the check.</param>
    /// <exception cref="InvalidOperationException">The header is malformed, or describes different data.</exception>
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
    /// <param name="sqlTypeOverrides">Override SQL types for specific columns whose storage form differs from the
    /// C# type default. Never override a <see cref="Guid"/> column: the whole stack addresses Guid keys as uppercase
    /// TEXT, and a row written in any other form is unreachable by key. A <c>[Column(TypeName="BLOB")]</c> annotation
    /// is not a reason to override — SQLite's BLOB affinity is "none", so a TEXT value stays TEXT in a BLOB column.</param>
    public static MessagePackFileHeaderV2 Create<T>(
        string tableName,
        string primaryKeyColumn,
        int recordCount,
        int mode = 0,
        string? appIdentifier = null,
        Dictionary<string, string>? sqlTypeOverrides = null)
    {
        var columns = BuildColumnMetadata(typeof(T), sqlTypeOverrides);

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
    public static string[][] BuildColumnMetadata(Type type, Dictionary<string, string>? sqlTypeOverrides = null)
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

            // Use override if provided, else detect type
            var propName = p.Property.Name;
            var sqlType = sqlTypeOverrides is not null && sqlTypeOverrides.TryGetValue(propName, out var overrideType)
                ? overrideType
                : GetSqlType(p.Property.PropertyType, propType);
            var csharpType = GetCsharpTypeName(p.Property.PropertyType, propType, isNullable);

            return new[] { p.Property.Name, sqlType, csharpType };
        }).ToArray();
    }

    /// <summary>
    /// Map C# type to SQLite column type, matching EF Core SQLite provider defaults.
    /// </summary>
    /// <param name="fullPropertyType">The full property type (including nullable wrapper, for collection detection)</param>
    /// <param name="underlyingType">The unwrapped non-nullable type</param>
    private static string GetSqlType(Type fullPropertyType, Type underlyingType)
    {
        // JSON collections (List<T>, IList<T>, etc.) → TEXT (EF Core value converter)
        if (IsJsonCollectionType(fullPropertyType))
        {
            return "TEXT";
        }

        // EF Core SQLite defaults (verified from migration snapshot):
        if (underlyingType == typeof(Guid))
        {
            // Uppercase TEXT is the one form the ADO layer binds and EF Core generates
            // literals in; anything else cannot be matched by a Guid key parameter.
            return "TEXT";
        }

        if (underlyingType == typeof(string))
        {
            return "TEXT";
        }

        if (underlyingType == typeof(bool))
        {
            return "INTEGER";
        }

        if (underlyingType == typeof(int) || underlyingType == typeof(long) || underlyingType == typeof(short)
            || underlyingType == typeof(byte) || underlyingType == typeof(uint) || underlyingType == typeof(ulong)
            || underlyingType == typeof(ushort) || underlyingType == typeof(sbyte))
        {
            return "INTEGER";
        }

        if (underlyingType == typeof(TimeSpan))
        {
            return "TEXT"; // EF Core default — stored as TimeSpan string format
        }

        if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
        {
            return "TEXT";
        }

        if (underlyingType == typeof(decimal))
        {
            return "TEXT";
        }

        if (underlyingType == typeof(double) || underlyingType == typeof(float))
        {
            return "REAL";
        }

        if (underlyingType == typeof(byte[]))
        {
            return "BLOB";
        }

        if (underlyingType == typeof(char))
        {
            return "TEXT";
        }

        if (underlyingType.IsEnum)
        {
            return "INTEGER";
        }

        return "TEXT";
    }

    /// <summary>
    /// Map C# type to csharpType name for worker-side type conversions.
    /// </summary>
    /// <param name="fullPropertyType">Full property type (for collection detection)</param>
    /// <param name="underlyingType">Unwrapped non-nullable type</param>
    /// <param name="isNullable">Whether the property is nullable</param>
    private static string GetCsharpTypeName(Type fullPropertyType, Type underlyingType, bool isNullable)
    {
        // JSON collections → "JsonArray" so worker knows to JSON.stringify/parse
        if (IsJsonCollectionType(fullPropertyType))
        {
            return "JsonArray";
        }

        // Handle enums — serialized as underlying integer by MessagePack
        if (underlyingType.IsEnum)
        {
            var name = "Enum";
            return isNullable ? $"{name}?" : name;
        }

        var typeName = underlyingType == typeof(byte[]) ? "ByteArray" : underlyingType.Name;

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

    private static bool IsJsonCollectionType(Type type)
    {
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            return genericDef == typeof(List<>) || genericDef == typeof(IList<>)
                || genericDef == typeof(ICollection<>) || genericDef == typeof(IEnumerable<>);
        }

        return false;
    }
}
