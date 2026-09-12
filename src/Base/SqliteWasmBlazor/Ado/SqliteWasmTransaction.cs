// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Data;
using System.Data.Common;

namespace SqliteWasmBlazor;

/// <summary>
/// Transaction that wraps BEGIN/COMMIT/ROLLBACK SQL commands.
/// </summary>
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

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel => _isolationLevel;

    /// <inheritdoc />
    protected override DbConnection DbConnection => _connection;

    /// <inheritdoc />
    public override void Commit()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        ExecuteNonQuery("COMMIT");
        _completed = true;
        _connection.ClearCurrentTransaction(this);
    }

    /// <inheritdoc />
    public override void Rollback()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        ExecuteNonQuery("ROLLBACK");
        _completed = true;
        _connection.ClearCurrentTransaction(this);
    }

    /// <inheritdoc />
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        await ExecuteNonQueryAsync("COMMIT", cancellationToken);
        _completed = true;
        _connection.ClearCurrentTransaction(this);
    }

    /// <inheritdoc />
    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }

        await ExecuteNonQueryAsync("ROLLBACK", cancellationToken);
        _completed = true;
        _connection.ClearCurrentTransaction(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The override that matters. <see cref="DbTransaction.DisposeAsync"/>
    /// defaults to calling <see cref="Dispose(bool)"/>, and the synchronous
    /// rollback that runs cannot reach the worker — there is no sync execution
    /// in WebAssembly, so it returns having sent nothing. An uncommitted
    /// transaction disposed that way is marked completed here while the worker
    /// still holds it open, and the next <c>BEGIN</c> on the connection fails
    /// with "cannot start a transaction within a transaction". EF disposes a
    /// failed transaction exactly this way, so the first migration to throw
    /// used to wedge every statement after it.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                await RollbackAsync();
            }
            finally
            {
                _connection.ClearCurrentTransaction(this);
            }
        }

        await base.DisposeAsync();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cannot roll back: synchronous execution does not exist in WebAssembly,
    /// so the ROLLBACK below is never sent and the worker keeps the
    /// transaction. Callers that can await must use
    /// <see cref="DisposeAsync"/>; this path only clears the bookkeeping so a
    /// leaked transaction does not also poison the connection object.
    /// </remarks>
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
            finally
            {
                _connection.ClearCurrentTransaction(this);
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
