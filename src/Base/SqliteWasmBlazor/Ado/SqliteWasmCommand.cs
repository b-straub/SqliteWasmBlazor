// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqliteWasmBlazor;

/// <summary>
/// Minimal SQLite command that sends SQL to worker for execution.
/// </summary>
public sealed class SqliteWasmCommand : DbCommand
{

    private string _commandText = string.Empty;
    private readonly SqliteWasmParameterCollection _parameters;

    /// <summary>Creates a command with no text and an empty parameter collection.</summary>
    public SqliteWasmCommand()
    {
        _parameters = new SqliteWasmParameterCollection();
    }

    /// <inheritdoc />
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int CommandTimeout { get; set; } = 30;

    /// <inheritdoc />
    public override CommandType CommandType { get; set; } = CommandType.Text;

    /// <inheritdoc />
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource { get; set; }

    /// <inheritdoc />
    protected override DbConnection? DbConnection { get; set; }

    /// <summary>
    /// The connection this command runs on, typed rather than as
    /// <see cref="DbConnection"/>. Shadows the base member; assigning either
    /// one sets the same underlying connection.
    /// </summary>
    public new SqliteWasmConnection? Connection
    {
        get => (SqliteWasmConnection?)DbConnection;
        set => DbConnection = value;
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>
    /// The command's parameters, typed rather than as
    /// <see cref="DbParameterCollection"/> so the
    /// <c>Add(name, value)</c> overload is reachable without a cast. Shadows
    /// the base member and returns the same collection.
    /// </summary>
    public new SqliteWasmParameterCollection Parameters => _parameters;

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction { get; set; }

    /// <inheritdoc />
    public override void Cancel()
    {
        // sqlite-wasm doesn't support cancellation in same way
    }

    /// <inheritdoc />
    public override int ExecuteNonQuery()
    {
        // Synchronous execution not supported in WebAssembly
        // Return 0 as EF Core will use async methods for actual work
        // This is primarily called during schema checks where return value isn't critical
        return 0;
    }
    
    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        ValidateConnection();

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);

        LogCommandSql(sql);

        var (parameterDict, packedBlobs) = _parameters.GetParameterValuesWithBlobs();
        var result = packedBlobs is null
            ? await bridge.ExecuteSqlAsync(Connection.Database, sql, parameterDict, cancellationToken)
            : await bridge.ExecuteSqlWithBlobsAsync(Connection.Database, sql, parameterDict, packedBlobs, cancellationToken);

        if (SqliteWasmLogger.CommandSqlLoggingEnabled)
        {
            Console.WriteLine($"[SqliteWasmCommand] Result: RowsAffected={result.RowsAffected}");
        }

        return result.RowsAffected;
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        // Synchronous execution not supported in WebAssembly
        // Return null as EF Core will use async methods for actual work
        return null;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        ValidateConnection();

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);
        LogCommandSql(sql);
        
        var (parameterDict, packedBlobs) = _parameters.GetParameterValuesWithBlobs();
        var result = packedBlobs is null
            ? await bridge.ExecuteSqlAsync(Connection.Database, sql, parameterDict, cancellationToken)
            : await bridge.ExecuteSqlWithBlobsAsync(Connection.Database, sql, parameterDict, packedBlobs, cancellationToken);

        if (result.Rows.Length > 0 && result.Rows[0].Length > 0)
        {
            return result.Rows[0][0];
        }

        return null;
    }

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        // Synchronous execution not supported in WebAssembly
        // Return empty reader as EF Core will use async methods for actual work
        var result = new SqlQueryResult();
        return new SqliteWasmDataReader(result);
    }

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ValidateConnection();

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);
        LogCommandSql(sql);

        var (parameterDict, packedBlobs) = _parameters.GetParameterValuesWithBlobs();
        var result = packedBlobs is null
            ? await bridge.ExecuteSqlAsync(Connection.Database, sql, parameterDict, cancellationToken)
            : await bridge.ExecuteSqlWithBlobsAsync(Connection.Database, sql, parameterDict, packedBlobs, cancellationToken);

        return new SqliteWasmDataReader(result);
    }

    /// <inheritdoc />
    public override void Prepare()
    {
        // No-op: sqlite-wasm handles preparation automatically
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter()
    {
        return new SqliteWasmParameter();
    }

    [MemberNotNull(nameof(Connection))]
    private void ValidateConnection()
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Connection property has not been initialized.");
        }

        if (Connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Connection must be Open.");
        }

        if (string.IsNullOrWhiteSpace(_commandText))
        {
            throw new InvalidOperationException("CommandText has not been set.");
        }
    }

    /// <summary>
    /// Preprocesses SQL to replace EF Core aggregate function names with native SQLite equivalents.
    /// This allows leveraging SQLite's native, optimized aggregate implementations.
    /// Arithmetic functions (ef_add, ef_multiply, etc.) are kept and handled by TypeScript.
    /// </summary>
    private static string PreprocessSql(string sql)
    {
        // Replace EF Core aggregate functions with native SQLite equivalents
        // Native SQLite aggregates are optimized and don't require custom state management
        sql = sql.Replace("ef_sum(", "sum(", StringComparison.OrdinalIgnoreCase);
        sql = sql.Replace("ef_avg(", "avg(", StringComparison.OrdinalIgnoreCase);
        sql = sql.Replace("ef_max(", "max(", StringComparison.OrdinalIgnoreCase);
        sql = sql.Replace("ef_min(", "min(", StringComparison.OrdinalIgnoreCase);

        return sql;
    }

    private void LogCommandSql(string sql)
    {
        if (!SqliteWasmLogger.CommandSqlLoggingEnabled)
        {
            return;
        }

        Console.WriteLine($"[SqliteWasmCommand] Executing SQL: {sql}");
        Console.WriteLine(
            $"[SqliteWasmCommand] Parameters: {string.Join(", ", _parameters.GetParameterValues().Select((v, i) => $"${i}={v}"))}");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _parameters.Clear();
        }
        base.Dispose(disposing);
    }
}
