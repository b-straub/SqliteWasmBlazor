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
    private readonly IDisposable _databaseTransactionScope;
    private readonly IsolationLevel _isolationLevel;
    private bool _completed;
    private bool _disposeRollbackScheduled;
    private bool _databaseTransactionScopeReleased;

    private SqliteWasmTransaction(
        SqliteWasmConnection connection,
        IsolationLevel isolationLevel,
        IDisposable databaseTransactionScope)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _isolationLevel = isolationLevel;
        _databaseTransactionScope = databaseTransactionScope;
    }

    internal static async Task<SqliteWasmTransaction> CreateAsync(
        SqliteWasmConnection connection,
        IsolationLevel isolationLevel,
        IDisposable databaseTransactionScope,
        CancellationToken cancellationToken = default)
    {
        var transaction = new SqliteWasmTransaction(connection, isolationLevel, databaseTransactionScope);
        await transaction.ExecuteNonQueryAsync(GetBeginSql(isolationLevel), cancellationToken);
        return transaction;
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    public override bool SupportsSavepoints => true;

    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        throw new NotSupportedException(
            "Synchronous transaction commit is not supported in WebAssembly. Use CommitAsync instead.");
    }

    public override void Rollback()
    {
        throw new NotSupportedException(
            "Synchronous transaction rollback is not supported in WebAssembly. Use RollbackAsync instead.");
    }

    public override void Save(string savepointName)
    {
        throw new NotSupportedException(
            "Synchronous transaction savepoints are not supported in WebAssembly. Use SaveAsync instead.");
    }

    public override void Rollback(string savepointName)
    {
        throw new NotSupportedException(
            "Synchronous transaction savepoint rollback is not supported in WebAssembly. Use RollbackAsync(savepointName) instead.");
    }

    public override void Release(string savepointName)
    {
        throw new NotSupportedException(
            "Synchronous transaction savepoint release is not supported in WebAssembly. Use ReleaseAsync instead.");
    }

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();

        await ExecuteNonQueryAsync("COMMIT", cancellationToken);
        _completed = true;
        _connection.ClearCurrentTransaction(this);
        ReleaseDatabaseTransactionScope();
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();

        await ExecuteNonQueryAsync("ROLLBACK", cancellationToken);
        _completed = true;
        _connection.ClearCurrentTransaction(this);
        ReleaseDatabaseTransactionScope();
    }

    public override async Task SaveAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await ExecuteNonQueryAsync($"SAVEPOINT {QuoteIdentifier(savepointName)}", cancellationToken);
    }

    public override async Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await ExecuteNonQueryAsync($"ROLLBACK TO SAVEPOINT {QuoteIdentifier(savepointName)}", cancellationToken);
    }

    public override async Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await ExecuteNonQueryAsync($"RELEASE SAVEPOINT {QuoteIdentifier(savepointName)}", cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && !_disposeRollbackScheduled)
        {
            _disposeRollbackScheduled = true;
            var cleanupTask = RollbackDuringDisposeAsync(CancellationToken.None);
            _connection.TrackPendingTransactionCleanup(cleanupTask);
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_completed && !_disposeRollbackScheduled)
        {
            _disposeRollbackScheduled = true;
            await RollbackDuringDisposeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (command is SqliteWasmCommand sqliteWasmCommand)
        {
            sqliteWasmCommand.SkipPendingTransactionCleanup = true;
            sqliteWasmCommand.SkipDatabaseTransactionGate = true;
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RollbackDuringDisposeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteNonQueryAsync("ROLLBACK", cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        catch
        {
            // Suppress exceptions during dispose.
        }
        finally
        {
            _connection.ClearCurrentTransaction(this);
            ReleaseDatabaseTransactionScope();
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }
    }

    private void ReleaseDatabaseTransactionScope()
    {
        if (_databaseTransactionScopeReleased)
        {
            return;
        }

        _databaseTransactionScopeReleased = true;
        _databaseTransactionScope.Dispose();
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Savepoint name must not be empty.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string GetBeginSql(IsolationLevel isolationLevel)
    {
        return isolationLevel switch
        {
            IsolationLevel.Unspecified => "BEGIN",
            IsolationLevel.ReadUncommitted => "BEGIN DEFERRED",
            IsolationLevel.ReadCommitted => "BEGIN DEFERRED",
            IsolationLevel.RepeatableRead => "BEGIN DEFERRED",
            IsolationLevel.Serializable => "BEGIN IMMEDIATE",
            IsolationLevel.Snapshot => "BEGIN IMMEDIATE",
            _ => throw new ArgumentException(
                $"Isolation level {isolationLevel} is not supported by SQLite.",
                nameof(isolationLevel))
        };
    }
}
