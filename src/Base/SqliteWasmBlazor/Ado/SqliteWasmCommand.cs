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
    internal static bool EnableCommandSqlLogging { get; set; }

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

    internal bool SkipPendingTransactionCleanup { get; set; }

    internal bool SkipDatabaseTransactionGate { get; set; }

    public override void Cancel()
    {
        // sqlite-wasm doesn't support cancellation in same way
    }

    public override int ExecuteNonQuery()
    {
        throw CreateSynchronousCommandNotSupportedException(nameof(ExecuteNonQueryAsync));
    }
    
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        ValidateConnection();
        await WaitForPendingTransactionCleanupAsync(cancellationToken).ConfigureAwait(false);
        await WaitForDatabaseTransactionAccessAsync(cancellationToken).ConfigureAwait(false);

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);

        LogCommandSql(sql);

        var (parameterDict, packedBlobs) = _parameters.GetParameterValuesWithBlobs();
        var result = packedBlobs is null
            ? await bridge.ExecuteSqlAsync(Connection.Database, sql, parameterDict, cancellationToken)
            : await bridge.ExecuteSqlWithBlobsAsync(Connection.Database, sql, parameterDict, packedBlobs, cancellationToken);

        if (EnableCommandSqlLogging)
        {
            Console.WriteLine($"[SqliteWasmCommand] Result: RowsAffected={result.RowsAffected}");
        }

        return result.RowsAffected;
    }

    public override object? ExecuteScalar()
    {
        throw CreateSynchronousCommandNotSupportedException(nameof(ExecuteScalarAsync));
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        ValidateConnection();
        await WaitForPendingTransactionCleanupAsync(cancellationToken).ConfigureAwait(false);
        await WaitForDatabaseTransactionAccessAsync(cancellationToken).ConfigureAwait(false);

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

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        throw CreateSynchronousCommandNotSupportedException(nameof(ExecuteReaderAsync));
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ValidateConnection();
        await WaitForPendingTransactionCleanupAsync(cancellationToken).ConfigureAwait(false);
        await WaitForDatabaseTransactionAccessAsync(cancellationToken).ConfigureAwait(false);

        var bridge = SqliteWasmWorkerBridge.Instance;
        var sql = PreprocessSql(_commandText);
        LogCommandSql(sql);
        var (parameterDict, packedBlobs) = _parameters.GetParameterValuesWithBlobs();
        var result = packedBlobs is null
            ? await bridge.ExecuteSqlAsync(Connection.Database, sql, parameterDict, cancellationToken)
            : await bridge.ExecuteSqlWithBlobsAsync(Connection.Database, sql, parameterDict, packedBlobs, cancellationToken);

        return new SqliteWasmDataReader(
            result,
            behavior.HasFlag(CommandBehavior.CloseConnection) ? Connection : null,
            GetReaderRecordsAffected(sql, result),
            behavior.HasFlag(CommandBehavior.SchemaOnly),
            behavior.HasFlag(CommandBehavior.SingleRow));
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

        if (CommandType != CommandType.Text)
        {
            throw new NotSupportedException(
                $"{nameof(SqliteWasmCommand)} only supports {nameof(CommandType)}.{nameof(CommandType.Text)}.");
        }
    }

    private static string PreprocessSql(string sql) => sql;

    private static int GetReaderRecordsAffected(string sql, SqlQueryResult result)
    {
        if (result.RowsAffected != 0 || result.ColumnNames.Count == 0)
        {
            return result.RowsAffected;
        }

        var keyword = GetPrimaryStatementKeyword(sql);
        return keyword is "SELECT" or "PRAGMA" or "EXPLAIN"
            ? -1
            : result.RowsAffected;
    }

    private static string GetPrimaryStatementKeyword(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            while (index < sql.Length && char.IsWhiteSpace(sql[index]))
            {
                index++;
            }

            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }
                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && (sql[index] != '*' || sql[index + 1] != '/'))
                {
                    index++;
                }
                index = Math.Min(index + 2, sql.Length);
                continue;
            }

            break;
        }

        var start = index;
        while (index < sql.Length && (char.IsLetter(sql[index]) || sql[index] == '_'))
        {
            index++;
        }

        return sql[start..index].ToUpperInvariant();
    }

    private void LogCommandSql(string sql)
    {
        if (!EnableCommandSqlLogging)
        {
            return;
        }

        Console.WriteLine($"[SqliteWasmCommand] Executing SQL: {sql}");
        Console.WriteLine(
            $"[SqliteWasmCommand] Parameters: {string.Join(", ", _parameters.GetParameterValues().Select((v, i) => $"${i}={v}"))}");
    }

    private Task WaitForPendingTransactionCleanupAsync(CancellationToken cancellationToken)
    {
        if (SkipPendingTransactionCleanup || Connection is null)
        {
            return Task.CompletedTask;
        }

        return Connection.WaitForPendingTransactionCleanupAsync(cancellationToken);
    }

    private Task WaitForDatabaseTransactionAccessAsync(CancellationToken cancellationToken)
    {
        if (SkipDatabaseTransactionGate || Connection is null)
        {
            return Task.CompletedTask;
        }

        return Connection.WaitForDatabaseTransactionAccessAsync(cancellationToken);
    }

    private static NotSupportedException CreateSynchronousCommandNotSupportedException(string asyncMethodName)
    {
        return new NotSupportedException(
            $"Synchronous command execution is not supported in WebAssembly. Use {asyncMethodName} instead.");
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
