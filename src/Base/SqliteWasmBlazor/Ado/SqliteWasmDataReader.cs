// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Text;

namespace SqliteWasmBlazor;

/// <summary>
/// DataReader that wraps results from sqlite-wasm worker.
/// </summary>
public sealed class SqliteWasmDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private const string SchemaColumnIsReadOnly = "IsReadOnly";
    private const string SchemaColumnIsRowVersion = "IsRowVersion";
    private const string SchemaColumnIsAutoIncrement = "IsAutoIncrement";
    private const string SchemaColumnBaseSchemaName = "BaseSchemaName";

    private readonly SqlQueryResult _result;
    private readonly SqliteWasmConnection? _closeConnection;
    private readonly int _recordsAffected;
    private readonly bool _schemaOnly;
    private readonly bool _singleRow;
    private int _currentRowIndex = -1;
    private bool _isClosed;

    internal SqliteWasmDataReader(
        SqlQueryResult result,
        SqliteWasmConnection? closeConnection = null,
        int? recordsAffected = null,
        bool schemaOnly = false,
        bool singleRow = false)
    {
        _result = result;
        _closeConnection = closeConnection;
        _recordsAffected = recordsAffected ?? result.RowsAffected;
        _schemaOnly = schemaOnly;
        _singleRow = singleRow;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        if (typeof(T) == typeof(Stream))
        {
            return (T)(object)GetStream(ordinal);
        }

        if (typeof(T) == typeof(TextReader))
        {
            return (T)(object)GetTextReader(ordinal);
        }

        if (typeof(T) == typeof(bool))
        {
            return (T)(object)GetBoolean(ordinal);
        }

        if (typeof(T) == typeof(byte))
        {
            return (T)(object)GetByte(ordinal);
        }

        if (typeof(T) == typeof(char))
        {
            return (T)(object)GetChar(ordinal);
        }

        if (typeof(T) == typeof(DateTime))
        {
            return (T)(object)GetDateTime(ordinal);
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            return (T)(object)GetDateTimeOffset(ordinal);
        }

        if (typeof(T) == typeof(DateOnly))
        {
            return (T)(object)GetDateOnly(ordinal);
        }

        if (typeof(T) == typeof(TimeOnly))
        {
            return (T)(object)GetTimeOnly(ordinal);
        }

        if (typeof(T) == typeof(decimal))
        {
            return (T)(object)GetDecimal(ordinal);
        }

        if (typeof(T) == typeof(double))
        {
            return (T)(object)GetDouble(ordinal);
        }

        if (typeof(T) == typeof(float))
        {
            return (T)(object)GetFloat(ordinal);
        }

        if (typeof(T) == typeof(Guid))
        {
            return (T)(object)GetGuid(ordinal);
        }

        if (typeof(T) == typeof(int))
        {
            return (T)(object)GetInt32(ordinal);
        }

        if (typeof(T) == typeof(long))
        {
            return (T)(object)GetInt64(ordinal);
        }

        checked
        {
            if (typeof(T) == typeof(sbyte))
            {
                return (T)(object)(sbyte)GetInt64(ordinal);
            }

            if (typeof(T) == typeof(short))
            {
                return (T)(object)GetInt16(ordinal);
            }

            if (typeof(T) == typeof(TimeSpan))
            {
                return (T)(object)GetTimeSpan(ordinal);
            }

            if (typeof(T) == typeof(uint))
            {
                return (T)(object)(uint)GetInt64(ordinal);
            }

            if (typeof(T) == typeof(ulong))
            {
                return (T)(object)unchecked((ulong)GetInt64(ordinal));
            }

            if (typeof(T) == typeof(ushort))
            {
                return (T)(object)(ushort)GetInt64(ordinal);
            }
        }

        if (IsDBNull(ordinal))
        {
            if (default(T) is not null)
            {
                throw new InvalidCastException($"Column {ordinal} is null and cannot be converted to {typeof(T).Name}.");
            }

            return default!;
        }

        return base.GetFieldValue<T>(ordinal);
    }

    public override int Depth => 0;

    public override int FieldCount => _result.ColumnNames.Count;

    public override bool HasRows => !_schemaOnly && _result.Rows.Length > 0;

    public override bool IsClosed => _isClosed;

    public override int RecordsAffected => _recordsAffected;

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

        if (dataOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataOffset));
        }

        if (dataOffset >= bytes.Length)
        {
            return 0;
        }

        var bytesToCopy = Math.Min(length, bytes.Length - (int)dataOffset);
        Array.Copy(bytes, (int)dataOffset, buffer, bufferOffset, bytesToCopy);
        return bytesToCopy;
    }

    public override char GetChar(int ordinal)
    {
        var value = GetValue(ordinal);

        // Handle single-character string (match Microsoft.Data.Sqlite behavior)
        if (value is string str)
        {
            if (str.Length == 1)
            {
                return str[0];
            }
            // For multi-char or empty strings, fall through to numeric conversion
        }

        return Convert.ToChar(value);
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetString(ordinal);
        if (buffer == null)
        {
            return value.Length;
        }

        if (dataOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataOffset));
        }

        if (dataOffset > value.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(dataOffset), dataOffset, null);
        }

        if (dataOffset == value.Length)
        {
            return 0;
        }

        var charsToCopy = Math.Min(length, value.Length - (int)dataOffset);
        for (var i = 0; i < charsToCopy; i++)
        {
            buffer[bufferOffset + i] = value[(int)dataOffset + i];
        }
        return charsToCopy;
    }

    public override Stream GetStream(int ordinal)
    {
        var value = GetValue(ordinal);

        if (value is byte[] bytes)
        {
            return new MemoryStream(bytes, writable: false);
        }

        if (value is string str)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(str), writable: false);
        }

        if (value is DBNull)
        {
            return new MemoryStream([], writable: false);
        }

        throw new InvalidCastException($"Column {ordinal} is not a stream-compatible value.");
    }

    public override TextReader GetTextReader(int ordinal)
    {
        if (IsDBNull(ordinal))
        {
            return new StringReader(string.Empty);
        }

        return new StreamReader(GetStream(ordinal), Encoding.UTF8);
    }

    public override string GetDataTypeName(int ordinal)
    {
        return _result.ColumnTypes[ordinal];
    }

    public override DateTime GetDateTime(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is DateTime dt)
        {
            return dt;
        }
        if (value is double d)
        {
            return FromJulianDate(d);
        }
        if (value is long l)
        {
            return FromJulianDate(l);
        }
        if (value is int i)
        {
            return FromJulianDate(i);
        }
        if (value is string str)
        {
            var dateTime = DateTime.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
            return dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime;
        }
        throw new InvalidCastException($"Column {ordinal} is not a DateTime. Actual type: {value.GetType().Name}");
    }

    public DateTimeOffset GetDateTimeOffset(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is DateTimeOffset dto)
        {
            return dto;
        }
        if (value is DateTime dt)
        {
            return new DateTimeOffset(dt);
        }
        if (value is double d)
        {
            return new DateTimeOffset(FromJulianDate(d), TimeSpan.Zero);
        }
        if (value is long l)
        {
            return new DateTimeOffset(FromJulianDate(l), TimeSpan.Zero);
        }
        if (value is int i)
        {
            return new DateTimeOffset(FromJulianDate(i), TimeSpan.Zero);
        }
        if (value is string str)
        {
            // Handle TEXT storage (SQLite stores DateTimeOffset as TEXT)
            // Match Microsoft.Data.Sqlite behavior
            return DateTimeOffset.Parse(str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        }
        throw new InvalidCastException($"Column {ordinal} is not a DateTimeOffset or DateTime. Actual type: {value.GetType().Name}");
    }

    public DateOnly GetDateOnly(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is DateOnly dateOnly)
        {
            return dateOnly;
        }
        if (value is DateTime dateTime)
        {
            return DateOnly.FromDateTime(dateTime);
        }
        if (value is double d)
        {
            return DateOnly.FromDateTime(FromJulianDate(d));
        }
        if (value is long l)
        {
            return DateOnly.FromDateTime(FromJulianDate(l));
        }
        if (value is int i)
        {
            return DateOnly.FromDateTime(FromJulianDate(i));
        }
        if (value is string str)
        {
            return DateOnly.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new InvalidCastException($"Column {ordinal} is not a DateOnly. Actual type: {value.GetType().Name}");
    }

    public TimeOnly GetTimeOnly(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is TimeOnly timeOnly)
        {
            return timeOnly;
        }
        if (value is TimeSpan timeSpan)
        {
            return TimeOnly.FromTimeSpan(timeSpan);
        }
        if (value is DateTime dateTime)
        {
            return TimeOnly.FromDateTime(dateTime);
        }
        if (value is string str)
        {
            return TimeOnly.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new InvalidCastException($"Column {ordinal} is not a TimeOnly. Actual type: {value.GetType().Name}");
    }

    public TimeSpan GetTimeSpan(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is TimeSpan ts)
        {
            return ts;
        }
        // Match Microsoft.Data.Sqlite behavior: FLOAT/INTEGER stored as days (not milliseconds!)
        if (value is double d)
        {
            return TimeSpan.FromDays(d);
        }
        if (value is long l)
        {
            return TimeSpan.FromDays(l);
        }
        if (value is int i)
        {
            return TimeSpan.FromDays(i);
        }
        if (value is string str)
        {
            return TimeSpan.Parse(str);
        }
        throw new InvalidCastException($"Column {ordinal} is not a TimeSpan. Actual type: {value.GetType().Name}");
    }

    public override decimal GetDecimal(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is string str)
        {
            return decimal.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
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
        ValidateOrdinal(ordinal);

        if (_currentRowIndex < 0 || _currentRowIndex >= _result.Rows.Length)
        {
            return MapSqliteTypeToClrType(GetDataTypeName(ordinal));
        }

        var value = _result.Rows[_currentRowIndex][ordinal];
        return value?.GetType() ?? MapSqliteTypeToClrType(GetDataTypeName(ordinal));
    }

    public override float GetFloat(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToSingle(value);
    }

    public override Guid GetGuid(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is Guid guid)
        {
            return guid;
        }
        if (value is string str)
        {
            return Guid.Parse(str);
        }
        if (value is byte[] bytes)
        {
            // Match Microsoft.Data.Sqlite behavior:
            // If 16 bytes, interpret as Guid directly
            // Otherwise, interpret as UTF-8 encoded Guid string
            return bytes.Length == 16
                ? new Guid(bytes)
                : new Guid(System.Text.Encoding.UTF8.GetString(bytes));
        }
        throw new InvalidCastException($"Column {ordinal} is not a Guid. Actual type: {value.GetType().Name}");
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
        ValidateOrdinal(ordinal);
        return _result.ColumnNames[ordinal];
    }

    public override int GetOrdinal(string name)
    {
        var exactIndex = _result.ColumnNames.IndexOf(name);
        if (exactIndex >= 0)
        {
            return exactIndex;
        }

        int? caseInsensitiveIndex = null;
        string? caseInsensitiveName = null;

        for (var i = 0; i < _result.ColumnNames.Count; i++)
        {
            var columnName = _result.ColumnNames[i];
            if (!string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (caseInsensitiveIndex.HasValue)
            {
                throw new InvalidOperationException(
                    $"Ambiguous column name '{name}' matched both '{caseInsensitiveName}' and '{columnName}'.");
            }

            caseInsensitiveIndex = i;
            caseInsensitiveName = columnName;
        }

        return caseInsensitiveIndex ??
            throw new ArgumentOutOfRangeException(nameof(name), name, null);
    }

    public override string GetString(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToString(value) ?? string.Empty;
    }

    public override object GetValue(int ordinal)
    {
        if (_currentRowIndex < 0 || _currentRowIndex >= _result.Rows.Length)
        {
            throw new InvalidOperationException("No current row.");
        }

        if (ordinal < 0 || ordinal >= _result.Rows[_currentRowIndex].Length)
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
        if (_currentRowIndex < 0 || _currentRowIndex >= _result.Rows.Length)
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

    public override DataTable GetSchemaTable()
    {
        var schemaTable = new DataTable("SchemaTable")
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture
        };

        schemaTable.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        schemaTable.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        schemaTable.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schemaTable.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        schemaTable.Columns.Add(SchemaColumnIsReadOnly, typeof(bool));
        schemaTable.Columns.Add(SchemaColumnIsRowVersion, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        schemaTable.Columns.Add(SchemaColumnIsAutoIncrement, typeof(bool));
        schemaTable.Columns.Add(SchemaColumnBaseSchemaName, typeof(string));
        schemaTable.Columns.Add(SchemaTableOptionalColumn.BaseCatalogName, typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));

        for (var ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            var dataTypeName = GetDataTypeName(ordinal);
            var row = schemaTable.NewRow();
            row[SchemaTableColumn.ColumnName] = GetName(ordinal);
            row[SchemaTableColumn.ColumnOrdinal] = ordinal;
            row[SchemaTableColumn.ColumnSize] = -1;
            row[SchemaTableColumn.NumericPrecision] = DBNull.Value;
            row[SchemaTableColumn.NumericScale] = DBNull.Value;
            row[SchemaTableColumn.DataType] = MapSqliteTypeToClrType(dataTypeName);
            row[SchemaTableColumn.ProviderType] = MapSqliteTypeToProviderType(dataTypeName);
            row[SchemaTableColumn.IsLong] = string.Equals(dataTypeName, "BLOB", StringComparison.OrdinalIgnoreCase);
            row[SchemaTableColumn.AllowDBNull] = true;
            row[SchemaColumnIsReadOnly] = false;
            row[SchemaColumnIsRowVersion] = false;
            row[SchemaTableColumn.IsUnique] = false;
            row[SchemaTableColumn.IsKey] = false;
            row[SchemaColumnIsAutoIncrement] = false;
            row[SchemaColumnBaseSchemaName] = string.Empty;
            row[SchemaTableOptionalColumn.BaseCatalogName] = string.Empty;
            row[SchemaTableColumn.BaseTableName] = string.Empty;
            row[SchemaTableColumn.BaseColumnName] = GetName(ordinal);
            schemaTable.Rows.Add(row);
        }

        return schemaTable;
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        var columns = new List<DbColumn>(FieldCount);

        for (var ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            var dataTypeName = GetDataTypeName(ordinal);
            columns.Add(new SqliteWasmDbColumn(
                columnName: GetName(ordinal),
                ordinal: ordinal,
                dataTypeName: dataTypeName,
                dataType: MapSqliteTypeToClrType(dataTypeName),
                isLong: string.Equals(dataTypeName, "BLOB", StringComparison.OrdinalIgnoreCase)));
        }

        return columns.AsReadOnly();
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

        if (_schemaOnly)
        {
            return false;
        }

        _currentRowIndex++;
        return _currentRowIndex < _result.Rows.Length &&
            (!_singleRow || _currentRowIndex == 0);
    }

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
    }

    public override void Close()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _closeConnection?.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
    }

    private static Type MapSqliteTypeToClrType(string sqliteType)
    {
        var normalized = sqliteType.ToUpperInvariant();
        if (normalized.Contains("INT", StringComparison.Ordinal))
        {
            return typeof(long);
        }

        if (normalized.Contains("REAL", StringComparison.Ordinal) ||
            normalized.Contains("FLOA", StringComparison.Ordinal) ||
            normalized.Contains("DOUB", StringComparison.Ordinal))
        {
            return typeof(double);
        }

        if (normalized.Contains("BLOB", StringComparison.Ordinal))
        {
            return typeof(byte[]);
        }

        return typeof(string);
    }

    private static int MapSqliteTypeToProviderType(string sqliteType)
    {
        var normalized = sqliteType.ToUpperInvariant();
        if (normalized.Contains("INT", StringComparison.Ordinal))
        {
            return (int)DbType.Int64;
        }

        if (normalized.Contains("REAL", StringComparison.Ordinal) ||
            normalized.Contains("FLOA", StringComparison.Ordinal) ||
            normalized.Contains("DOUB", StringComparison.Ordinal))
        {
            return (int)DbType.Double;
        }

        if (normalized.Contains("BLOB", StringComparison.Ordinal))
        {
            return (int)DbType.Binary;
        }

        return (int)DbType.String;
    }

    private static DateTime FromJulianDate(double julianDate)
    {
        var days = julianDate - 1721425.5;
        return DateTime.MinValue.AddDays(days);
    }

    private sealed class SqliteWasmDbColumn : DbColumn
    {
        public SqliteWasmDbColumn(
            string columnName,
            int ordinal,
            string dataTypeName,
            Type dataType,
            bool isLong)
        {
            AllowDBNull = true;
            BaseCatalogName = string.Empty;
            BaseColumnName = columnName;
            BaseSchemaName = string.Empty;
            BaseTableName = string.Empty;
            ColumnName = columnName;
            ColumnOrdinal = ordinal;
            ColumnSize = -1;
            DataType = dataType;
            DataTypeName = dataTypeName;
            IsAutoIncrement = false;
            IsKey = false;
            IsLong = isLong;
            IsReadOnly = false;
            IsUnique = false;
        }
    }
}
