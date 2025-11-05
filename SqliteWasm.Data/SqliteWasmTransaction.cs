// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using System.Data.Common;
using System.Runtime.Versioning;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// Transaction that wraps BEGIN/COMMIT/ROLLBACK SQL commands.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class SqliteWasmTransaction : DbTransaction
{
    private readonly SqliteWasmConnection _connection;
    private readonly IsolationLevel _isolationLevel;
    private bool _completed;

    private SqliteWasmTransaction(SqliteWasmConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _isolationLevel = isolationLevel;
    }

    internal static async Task<SqliteWasmTransaction> CreateAsync(
        SqliteWasmConnection connection,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        var transaction = new SqliteWasmTransaction(connection, isolationLevel);
        await transaction.ExecuteNonQueryAsync(GetBeginSql(isolationLevel), cancellationToken);
        return transaction;
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        ExecuteNonQuery("COMMIT");
        _completed = true;
    }

    public override void Rollback()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        ExecuteNonQuery("ROLLBACK");
        _completed = true;
    }

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        await ExecuteNonQueryAsync("COMMIT", cancellationToken);
        _completed = true;
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        await ExecuteNonQueryAsync("ROLLBACK", cancellationToken);
        _completed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            try
            {
                Rollback();
            }
            catch
            {
                // Suppress exceptions during dispose
            }
        }
        base.Dispose(disposing);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetBeginSql(IsolationLevel isolationLevel)
    {
        return isolationLevel switch
        {
            IsolationLevel.ReadUncommitted => "BEGIN DEFERRED",
            IsolationLevel.ReadCommitted => "BEGIN DEFERRED",
            IsolationLevel.RepeatableRead => "BEGIN DEFERRED",
            IsolationLevel.Serializable => "BEGIN IMMEDIATE",
            IsolationLevel.Snapshot => "BEGIN IMMEDIATE",
            _ => "BEGIN"
        };
    }
}
