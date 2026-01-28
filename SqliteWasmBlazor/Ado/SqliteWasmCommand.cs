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

    public SqliteWasmCommand()
    {
        _parameters = new SqliteWasmParameterCollection();
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    public new SqliteWasmConnection? Connection
    {
        get => (SqliteWasmConnection?)DbConnection;
        set => DbConnection = value;
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new SqliteWasmParameterCollection Parameters => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
        // sqlite-wasm doesn't support cancellation in same way
    }

    public override int ExecuteNonQuery()
    {
        // Synchronous execution not supported in WebAssembly
        // Return 0 as EF Core will use async methods for actual work
        // This is primarily called during schema checks where return value isn't critical
        return 0;
    }
    
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        ValidateConnection();

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);

        // DEBUG: Log UPDATE operations
        if (sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[SqliteWasmCommand] Executing UPDATE: {sql}");
            Console.WriteLine($"[SqliteWasmCommand] Parameters: {string.Join(", ", _parameters.GetParameterValues().Select((v, i) => $"${i}={v}"))}");
        }

        var result = await bridge.ExecuteSqlAsync(
            Connection.Database,
            sql,
            _parameters.GetParameterValues(),
            cancellationToken);

        // DEBUG: Log result of UPDATE operations
        if (sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[SqliteWasmCommand] UPDATE result: RowsAffected={result.RowsAffected}");
        }

        return result.RowsAffected;
    }

    public override object? ExecuteScalar()
    {
        // Synchronous execution not supported in WebAssembly
        // Return null as EF Core will use async methods for actual work
        return null;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        ValidateConnection();

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);
        var result = await bridge.ExecuteSqlAsync(
            Connection.Database,
            sql,
            _parameters.GetParameterValues(),
            cancellationToken);

        if (result.Rows.Length > 0 && result.Rows[0].Length > 0)
        {
            return result.Rows[0][0];
        }

        return null;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        // Synchronous execution not supported in WebAssembly
        // Return empty reader as EF Core will use async methods for actual work
        var result = new SqlQueryResult();
        return new SqliteWasmDataReader(result);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ValidateConnection();

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);
        var result = await bridge.ExecuteSqlAsync(
            Connection.Database,
            sql,
            _parameters.GetParameterValues(),
            cancellationToken);

        return new SqliteWasmDataReader(result);
    }

    public override void Prepare()
    {
        // No-op: sqlite-wasm handles preparation automatically
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _parameters.Clear();
        }
        base.Dispose(disposing);
    }
}
