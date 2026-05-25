// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace SqliteWasmBlazor;

/// <summary>
/// Represents a parameter to a SqliteWasmCommand.
/// </summary>
public sealed class SqliteWasmParameter : DbParameter
{
    private string _parameterName = string.Empty;
    private object? _value;
    private DbType _dbType = DbType.Object;
    private string _sourceColumn = string.Empty;

    public SqliteWasmParameter()
    {
    }

    public SqliteWasmParameter(string parameterName, object? value)
    {
        _parameterName = parameterName;
        _value = value;
    }

    public SqliteWasmParameter(string parameterName, DbType dbType)
    {
        _parameterName = parameterName;
        _dbType = dbType;
    }

    public override DbType DbType
    {
        get => _dbType;
        set => _dbType = value;
    }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = value ?? string.Empty;
    }

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value
    {
        get => _value;
        set => _value = value;
    }

    public override void ResetDbType()
    {
        _dbType = DbType.Object;
    }
}

/// <summary>
/// Collection of parameters for SqliteWasmCommand.
/// </summary>
public sealed class SqliteWasmParameterCollection : DbParameterCollection
{
    /// <summary>
    /// JS Number.MAX_SAFE_INTEGER (2^53 - 1). Integer values beyond this range
    /// lose precision when serialized as JSON numbers.
    /// </summary>
    private const long MaxSafeInteger = 9007199254740991L;

    private readonly List<SqliteWasmParameter> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot => ((System.Collections.ICollection)_parameters).SyncRoot;

    public override int Add(object value)
    {
        if (value is not SqliteWasmParameter parameter)
        {
            throw new ArgumentException("Value must be a SqliteWasmParameter.", nameof(value));
        }

        _parameters.Add(parameter);
        return _parameters.Count - 1;
    }

    public SqliteWasmParameter Add(string parameterName, object? value)
    {
        var parameter = new SqliteWasmParameter(parameterName, value);
        _parameters.Add(parameter);
        return parameter;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value);
        }
    }

    public override void Clear()
    {
        _parameters.Clear();
    }

    public override bool Contains(object value)
    {
        return value is SqliteWasmParameter parameter && _parameters.Contains(parameter);
    }

    public override bool Contains(string value)
    {
        return IndexOf(value) >= 0;
    }

    public override void CopyTo(Array array, int index)
    {
        ((System.Collections.ICollection)_parameters).CopyTo(array, index);
    }

    public override System.Collections.IEnumerator GetEnumerator()
    {
        return ((System.Collections.IEnumerable)_parameters).GetEnumerator();
    }

    public override int IndexOf(object value)
    {
        return value is SqliteWasmParameter parameter ? _parameters.IndexOf(parameter) : -1;
    }

    public override int IndexOf(string parameterName)
    {
        return _parameters.FindIndex(p => ParameterNamesMatch(p.ParameterName, parameterName));
    }

    public override void Insert(int index, object value)
    {
        _parameters.Insert(index, (SqliteWasmParameter)value);
    }

    public override void Remove(object value)
    {
        _parameters.Remove((SqliteWasmParameter)value);
    }

    public override void RemoveAt(int index)
    {
        _parameters.RemoveAt(index);
    }

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
        {
            RemoveAt(index);
        }
    }

    protected override DbParameter GetParameter(int index)
    {
        return _parameters[index];
    }

    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        }
        return _parameters[index];
    }

    protected override void SetParameter(int index, DbParameter value)
    {
        _parameters[index] = (SqliteWasmParameter)value;
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        }
        _parameters[index] = (SqliteWasmParameter)value;
    }

    /// <summary>
    /// Gets parameter values as dictionary for sending to worker.
    /// Each parameter includes value and type metadata for proper SQLite binding.
    /// </summary>
    internal Dictionary<string, object?> GetParameterValues()
    {
        var result = new Dictionary<string, object?>();
        foreach (var param in _parameters)
        {
            var converted = ConvertParameterValue(param);
            var value = converted.Value;
            if (converted.BlobBytes is not null)
            {
                value = Convert.ToBase64String(converted.BlobBytes);
            }

            // Send parameter with type metadata
            var typedValue = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["type"] = converted.SqliteType
            };
            AddParameterBindings(result, param.ParameterName, typedValue);
        }
        return result;
    }

    /// <summary>
    /// Same as <see cref="GetParameterValues"/> but extracts
    /// <see cref="byte"/>[] blob values into a side-channel packed byte
    /// buffer instead of Base64-encoding them into the JSON message. Each
    /// blob entry in the returned dict becomes
    /// <c>{ value: { __blobOffset: N, __blobLength: L }, type: "blob" }</c>
    /// pointing into the packed buffer; the worker reads the bytes from
    /// the binary attachment via <c>SendBinaryToWorker</c>.
    ///
    /// <para>
    /// Used by every <see cref="SqliteWasmCommand"/> async execute path —
    /// when any parameter is <see cref="byte"/>[], the bridge routes
    /// through <see cref="SqliteWasmWorkerBridge.ExecuteSqlWithBlobsAsync"/>
    /// instead of the JSON-only <c>ExecuteSqlAsync</c>. Eliminates the
    /// per-blob <c>Convert.ToBase64String</c> + JSON-string chain that
    /// otherwise allocated ~7×blob-size in transient memory per write.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>(dict, packedBlobs)</c>. <c>packedBlobs</c> is <c>null</c> if no
    /// byte[] params were present (caller falls through to the JSON-only path).
    /// </returns>
    internal (Dictionary<string, object?> Parameters, byte[]? PackedBlobs) GetParameterValuesWithBlobs()
    {
        // First pass — count blob param size to size the packed buffer.
        var totalBlobBytes = 0;
        foreach (var param in _parameters)
        {
            var converted = ConvertParameterValue(param);
            if (converted.BlobBytes is byte[] b)
            {
                totalBlobBytes += b.Length;
            }
        }

        if (totalBlobBytes == 0)
        {
            return (GetParameterValues(), null);
        }

        var packed = new byte[totalBlobBytes];
        var offset = 0;
        var result = new Dictionary<string, object?>();
        foreach (var param in _parameters)
        {
            var converted = ConvertParameterValue(param);
            var value = converted.Value;

            if (converted.BlobBytes is byte[] bytes)
            {
                Buffer.BlockCopy(bytes, 0, packed, offset, bytes.Length);
                value = new Dictionary<string, object?>
                {
                    ["__blobOffset"] = offset,
                    ["__blobLength"] = bytes.Length,
                };
                offset += bytes.Length;
            }

            var typedValue = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["type"] = converted.SqliteType,
            };
            AddParameterBindings(result, param.ParameterName, typedValue);
        }
        return (result, packed);
    }

    private static (object? Value, string SqliteType, byte[]? BlobBytes) ConvertParameterValue(
        SqliteWasmParameter parameter)
    {
        var value = parameter.Value;
        if (value is null or DBNull)
        {
            return (null, "null", null);
        }

        if (parameter.DbType != DbType.Object)
        {
            return ConvertExplicitDbType(value, parameter.DbType);
        }

        return InferParameterValue(value);
    }

    private static (object? Value, string SqliteType, byte[]? BlobBytes) ConvertExplicitDbType(
        object value,
        DbType dbType)
    {
        switch (dbType)
        {
            case DbType.Binary:
                return value is byte[] bytes
                    ? (null, "blob", bytes)
                    : (null, "blob", Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));

            case DbType.Boolean:
                return (Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0, "integer", null);

            case DbType.Byte:
            case DbType.SByte:
            case DbType.Int16:
            case DbType.UInt16:
            case DbType.Int32:
            case DbType.UInt32:
            case DbType.Int64:
            case DbType.UInt64:
                return ConvertInteger(value, dbType);

            case DbType.Single:
            case DbType.Double:
            case DbType.Currency:
                return (Convert.ToDouble(value, CultureInfo.InvariantCulture), "real", null);

            case DbType.Decimal:
                return (Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), "text", null);

            case DbType.Date:
                return value switch
                {
                    DateOnly dateOnly => (dateOnly.ToString("O", CultureInfo.InvariantCulture), "text", null),
                    DateTime dateTime => (dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "text", null),
                    DateTimeOffset dtoDate => (dtoDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "text", null),
                    _ => (Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "text", null),
                };

            case DbType.DateTime:
            case DbType.DateTime2:
                return value switch
                {
                    DateOnly dateOnly => (dateOnly.ToDateTime(TimeOnly.MinValue).ToString("O", CultureInfo.InvariantCulture), "text", null),
                    DateTime dateTime => (dateTime.ToString("O", CultureInfo.InvariantCulture), "text", null),
                    DateTimeOffset dtoDateTime => (dtoDateTime.ToString("O", CultureInfo.InvariantCulture), "text", null),
                    _ => (Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture), "text", null),
                };

            case DbType.DateTimeOffset:
                var dateTimeOffset = value is DateTimeOffset dto
                    ? dto
                    : new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture));
                return (dateTimeOffset.ToString("O", CultureInfo.InvariantCulture), "text", null);

            case DbType.Time:
                return value switch
                {
                    TimeOnly timeOnly => (timeOnly.ToString("O", CultureInfo.InvariantCulture), "text", null),
                    TimeSpan timeSpan => (timeSpan.ToString("c", CultureInfo.InvariantCulture), "text", null),
                    _ => (Convert.ToString(value, CultureInfo.InvariantCulture), "text", null),
                };

            default:
                return (Convert.ToString(value, CultureInfo.InvariantCulture), "text", null);
        }
    }

    private static (object? Value, string SqliteType, byte[]? BlobBytes) InferParameterValue(object value)
    {
        if (value is DateTime dt)
        {
            return (dt.ToString("O", CultureInfo.InvariantCulture), "text", null);
        }
        if (value is DateTimeOffset dto)
        {
            return (dto.ToString("O", CultureInfo.InvariantCulture), "text", null);
        }
        if (value is DateOnly dateOnly)
        {
            return (dateOnly.ToString("O", CultureInfo.InvariantCulture), "text", null);
        }
        if (value is TimeOnly timeOnly)
        {
            return (timeOnly.ToString("O", CultureInfo.InvariantCulture), "text", null);
        }
        if (value is Guid guid)
        {
            return (guid.ToString().ToUpperInvariant(), "text", null);
        }
        if (value is byte[] bytes)
        {
            return (null, "blob", bytes);
        }
        if (value is bool boolean)
        {
            return (boolean ? 1 : 0, "integer", null);
        }
        if (value is long l and (> MaxSafeInteger or < -MaxSafeInteger))
        {
            return (l.ToString(CultureInfo.InvariantCulture), "text", null);
        }
        if (value is ulong ul and > MaxSafeInteger)
        {
            return (ul.ToString(CultureInfo.InvariantCulture), "text", null);
        }
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            return (value, "integer", null);
        }
        if (value is decimal decimalValue)
        {
            return (decimalValue.ToString(CultureInfo.InvariantCulture), "text", null);
        }
        if (value is float or double)
        {
            return (value, "real", null);
        }
        if (value is string)
        {
            return (value, "text", null);
        }

        return (Convert.ToString(value, CultureInfo.InvariantCulture), "text", null);
    }

    private static (object? Value, string SqliteType, byte[]? BlobBytes) ConvertInteger(
        object value,
        DbType dbType)
    {
        var text = dbType == DbType.UInt64
            ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed) &&
            signed is <= MaxSafeInteger and >= -MaxSafeInteger)
        {
            return (signed, "integer", null);
        }

        return (text, "text", null);
    }

    private static bool ParameterNamesMatch(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal)
            || string.Equals(TrimParameterPrefix(left), TrimParameterPrefix(right), StringComparison.Ordinal);
    }

    private static string TrimParameterPrefix(string parameterName)
    {
        return parameterName.Length > 0 && IsParameterPrefix(parameterName[0])
            ? parameterName[1..]
            : parameterName;
    }

    private static bool HasParameterPrefix(string parameterName)
    {
        return parameterName.Length > 0 && IsParameterPrefix(parameterName[0]);
    }

    private static bool IsParameterPrefix(char ch)
    {
        return ch is '@' or '$' or ':';
    }

    private static void AddParameterBindings(
        Dictionary<string, object?> result,
        string parameterName,
        Dictionary<string, object?> typedValue)
    {
        if (string.IsNullOrEmpty(parameterName))
        {
            result[parameterName] = typedValue;
            return;
        }

        if (HasParameterPrefix(parameterName))
        {
            result[parameterName] = typedValue;
            return;
        }

        result["@" + parameterName] = typedValue;
        result["$" + parameterName] = typedValue;
        result[":" + parameterName] = typedValue;
    }
}
