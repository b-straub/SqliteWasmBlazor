// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using System.Collections;
using System.Data.Common;
using System.Runtime.Versioning;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// DataReader that wraps results from sqlite-wasm worker.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class SqliteWasmDataReader : DbDataReader
{
    private readonly SqlQueryResult _result;
    private int _currentRowIndex = -1;
    private bool _isClosed;

    internal SqliteWasmDataReader(SqlQueryResult result)
    {
        _result = result;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        // Special handling for DateTimeOffset since there's no GetDateTimeOffset() in DbDataReader
        if (typeof(T) == typeof(DateTimeOffset))
        {
            return (T)(object)GetDateTimeOffset(ordinal);
        }

        // Special handling for TimeSpan
        if (typeof(T) == typeof(TimeSpan))
        {
            var value = GetValue(ordinal);
            return (T)(object)TimeSpan.Parse(Convert.ToString(value) ?? string.Empty);
        }

        // Default behavior for all other types
        return base.GetFieldValue<T>(ordinal);
    }

    public override int Depth => 0;

    public override int FieldCount => _result.ColumnNames.Count;

    public override bool HasRows => _result.Rows.Count > 0;

    public override bool IsClosed => _isClosed;

    public override int RecordsAffected => _result.RowsAffected;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool GetBoolean(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToBoolean(value);
    }

    public override byte GetByte(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToByte(value);
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var value = GetValue(ordinal);
        byte[] bytes;

        if (value is byte[] byteArray)
        {
            bytes = byteArray;
        }
        else
        {
            throw new InvalidCastException($"Column {ordinal} is not a byte array.");
        }

        if (buffer == null)
        {
            return bytes.Length;
        }

        var bytesToCopy = Math.Min(length, bytes.Length - (int)dataOffset);
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, bytesToCopy);
        return bytesToCopy;
    }

    public override char GetChar(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToChar(value);
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetString(ordinal);
        if (buffer == null)
        {
            return value.Length;
        }

        var charsToCopy = Math.Min(length, value.Length - (int)dataOffset);
        value.CopyTo((int)dataOffset, buffer, bufferOffset, charsToCopy);
        return charsToCopy;
    }

    public override string GetDataTypeName(int ordinal)
    {
        return _result.ColumnTypes[ordinal];
    }

    public override DateTime GetDateTime(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is string str)
        {
            return DateTime.Parse(str, null, Globalization.DateTimeStyles.RoundtripKind);
        }
        return Convert.ToDateTime(value);
    }

    public DateTimeOffset GetDateTimeOffset(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is string str)
        {
            return DateTimeOffset.Parse(str, null, Globalization.DateTimeStyles.RoundtripKind);
        }
        if (value is DateTimeOffset dto)
        {
            return dto;
        }
        if (value is DateTime dt)
        {
            return new DateTimeOffset(dt);
        }
        return DateTimeOffset.Parse(Convert.ToString(value) ?? string.Empty);
    }

    public override decimal GetDecimal(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is string str)
        {
            return decimal.Parse(str, Globalization.CultureInfo.InvariantCulture);
        }
        return Convert.ToDecimal(value);
    }

    public override double GetDouble(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToDouble(value);
    }

    public override Type GetFieldType(int ordinal)
    {
        if (_currentRowIndex < 0 || _currentRowIndex >= _result.Rows.Count)
        {
            // Return string as default if no data yet
            return typeof(string);
        }

        var value = _result.Rows[_currentRowIndex][ordinal];
        return value?.GetType() ?? typeof(object);
    }

    public override float GetFloat(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToSingle(value);
    }

    public override Guid GetGuid(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is string str)
        {
            return Guid.Parse(str);
        }
        if (value is byte[] bytes)
        {
            return new Guid(bytes);
        }
        throw new InvalidCastException($"Cannot convert column {ordinal} to Guid.");
    }

    public override short GetInt16(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToInt16(value);
    }

    public override int GetInt32(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToInt32(value);
    }

    public override long GetInt64(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToInt64(value);
    }

    public override string GetName(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.ColumnNames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        return _result.ColumnNames[ordinal];
    }

    public override int GetOrdinal(string name)
    {
        var index = _result.ColumnNames.IndexOf(name);
        if (index < 0)
        {
            throw new ArgumentException($"Column '{name}' not found.", nameof(name));
        }
        return index;
    }

    public override string GetString(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToString(value) ?? string.Empty;
    }

    public override object GetValue(int ordinal)
    {
        if (_currentRowIndex < 0 || _currentRowIndex >= _result.Rows.Count)
        {
            throw new InvalidOperationException("No current row.");
        }

        if (ordinal < 0 || ordinal >= _result.Rows[_currentRowIndex].Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var value = _result.Rows[_currentRowIndex][ordinal];

        if (value is null)
        {
            return DBNull.Value;
        }

        return value;
    }

    public override int GetValues(object[] values)
    {
        if (_currentRowIndex < 0 || _currentRowIndex >= _result.Rows.Count)
        {
            throw new InvalidOperationException("No current row.");
        }

        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }
        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        var value = GetValue(ordinal);
        return value is DBNull;
    }

    public override bool NextResult()
    {
        // SQLite doesn't support multiple result sets
        return false;
    }

    public override bool Read()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("DataReader is closed.");
        }

        _currentRowIndex++;
        return _currentRowIndex < _result.Rows.Count;
    }

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
    }

    public override void Close()
    {
        _isClosed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }
}
