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
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    /// There is no synchronous execution in WebAssembly, so a synchronous
    /// COMMIT cannot be sent. It used to return as if it had been, marking the
    /// transaction complete while the worker still held it open — every write
    /// in it then quietly rolled back when the connection closed. Throwing is
    /// the only honest answer; use <see cref="CommitAsync"/>.
    /// </remarks>
    public override void Commit() =>
        throw new NotSupportedException(
            "Synchronous Commit is not available in WebAssembly. Use CommitAsync.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>Same as <see cref="Commit"/>; use <see cref="RollbackAsync"/>.</remarks>
    public override void Rollback() =>
        throw new NotSupportedException(
            "Synchronous Rollback is not available in WebAssembly. Use RollbackAsync.");

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
    /// <para>
    /// Cannot await, so it cannot confirm a rollback — but it can still send
    /// one. The bridge posts a request to the worker before its first await,
    /// and the worker runs requests in order, so a ROLLBACK issued here lands
    /// ahead of any BEGIN that follows. That is the same arrangement
    /// <see cref="SqliteWasmConnection.Open"/> relies on. What is lost is only
    /// the result: a rollback that fails is written to the error stream rather
    /// than thrown, because a throw out of Dispose replaces whatever exception
    /// caused the transaction to be abandoned in the first place.
    /// </para>
    /// <para>
    /// Prefer <c>await using</c>, which reaches <see cref="DisposeAsync"/> and
    /// does await it.
    /// </para>
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            _completed = true;
            _connection.ClearCurrentTransaction(this);

            try
            {
                ObserveFaults(ExecuteNonQueryAsync("ROLLBACK", CancellationToken.None));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[SqliteWasmTransaction] ROLLBACK on synchronous dispose could not be sent: {ex.Message}");
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Keeps a fire-and-forget rollback from dying silently: its fault is the
    /// one thing a synchronous dispose cannot surface any other way.
    /// </summary>
    private static void ObserveFaults(Task rollback)
    {
        _ = rollback.ContinueWith(
            static t =>
            {
                var message = t.Exception is { } ex ? ex.GetBaseException().Message : "unknown";
                Console.Error.WriteLine(
                    $"[SqliteWasmTransaction] ROLLBACK on synchronous dispose failed: {message}");
            },
            TaskContinuationOptions.OnlyOnFaulted);
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
