// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

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
        return _parameters.Contains((SqliteWasmParameter)value);
    }

    public override bool Contains(string value)
    {
        return _parameters.Any(p => p.ParameterName == value);
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
        return _parameters.IndexOf((SqliteWasmParameter)value);
    }

    public override int IndexOf(string parameterName)
    {
        return _parameters.FindIndex(p => p.ParameterName == parameterName);
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
            // Convert parameter value to JSON-serializable primitive
            var value = param.Value;
            string sqliteType;

            if (value is null or DBNull)
            {
                value = null;
                sqliteType = "null";
            }
            else if (value is DateTime dt)
            {
                // Convert DateTime to ISO 8601 string for JSON serialization
                value = dt.ToString("O");
                sqliteType = "text";
            }
            else if (value is DateTimeOffset dto)
            {
                value = dto.ToString("O");
                sqliteType = "text";
            }
            else if (value is Guid guid)
            {
                // Match the uppercase TEXT format that EF Core's EnsureCreated/HasData
                // generates for Guid INSERT literals.
                value = guid.ToString().ToUpperInvariant();
                sqliteType = "text";
            }
            else if (value is byte[] bytes)
            {
                value = Convert.ToBase64String(bytes);
                sqliteType = "blob";
            }
            else if (value is bool)
            {
                // SQLite stores booleans as integers
                sqliteType = "integer";
            }
            else if (value is long l and (> MaxSafeInteger or < -MaxSafeInteger))
            {
                // JS Number can only represent integers up to 2^53-1 precisely.
                // JSON serialization of larger values loses precision (e.g., long.MaxValue → wrong value).
                // Send as text — SQLite INTEGER affinity coerces text→int64 correctly in C code.
                value = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
                sqliteType = "text";
            }
            else if (value is ulong ul and > MaxSafeInteger)
            {
                value = ul.ToString(System.Globalization.CultureInfo.InvariantCulture);
                sqliteType = "text";
            }
            else if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
            {
                sqliteType = "integer";
            }
            else if (value is float or double or decimal)
            {
                sqliteType = "real";
            }
            else if (value is string)
            {
                sqliteType = "text";
            }
            else
            {
                // Fallback for unknown types
                sqliteType = "text";
            }

            // Ensure parameter name has @ prefix for SQLite compatibility
            var paramName = param.ParameterName;
            if (!string.IsNullOrEmpty(paramName) && !paramName.StartsWith('@') && !paramName.StartsWith('$') && !paramName.StartsWith(':'))
            {
                paramName = "@" + paramName;
            }

            // Send parameter with type metadata
            result[paramName] = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["type"] = sqliteType
            };
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
            if (param.Value is byte[] b)
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
            var value = param.Value;
            string sqliteType;

            if (value is null or DBNull)
            {
                value = null;
                sqliteType = "null";
            }
            else if (value is DateTime dt)
            {
                value = dt.ToString("O");
                sqliteType = "text";
            }
            else if (value is DateTimeOffset dto)
            {
                value = dto.ToString("O");
                sqliteType = "text";
            }
            else if (value is Guid guid)
            {
                value = guid.ToString().ToUpperInvariant();
                sqliteType = "text";
            }
            else if (value is byte[] bytes)
            {
                Buffer.BlockCopy(bytes, 0, packed, offset, bytes.Length);
                value = new Dictionary<string, object?>
                {
                    ["__blobOffset"] = offset,
                    ["__blobLength"] = bytes.Length,
                };
                offset += bytes.Length;
                sqliteType = "blob";
            }
            else if (value is bool)
            {
                sqliteType = "integer";
            }
            else if (value is long l and (> MaxSafeInteger or < -MaxSafeInteger))
            {
                value = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
                sqliteType = "text";
            }
            else if (value is ulong ul and > MaxSafeInteger)
            {
                value = ul.ToString(System.Globalization.CultureInfo.InvariantCulture);
                sqliteType = "text";
            }
            else if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
            {
                sqliteType = "integer";
            }
            else if (value is float or double or decimal)
            {
                sqliteType = "real";
            }
            else if (value is string)
            {
                sqliteType = "text";
            }
            else
            {
                sqliteType = "text";
            }

            var paramName = param.ParameterName;
            if (!string.IsNullOrEmpty(paramName) && !paramName.StartsWith('@') && !paramName.StartsWith('$') && !paramName.StartsWith(':'))
            {
                paramName = "@" + paramName;
            }

            result[paramName] = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["type"] = sqliteType,
            };
        }
        return (result, packed);
    }
}
