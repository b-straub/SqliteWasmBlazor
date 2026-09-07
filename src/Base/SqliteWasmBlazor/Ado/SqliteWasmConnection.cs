// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqliteWasmBlazor;

/// <summary>
/// Minimal SQLite connection for EF Core using sqlite-wasm + OPFS.
/// </summary>
public sealed class SqliteWasmConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private ConnectionState _state = ConnectionState.Closed;
    private readonly SqliteWasmWorkerBridge _bridge;
    private SqliteWasmTransaction? _currentTransaction;

    /// <summary>
    /// Creates a connection with no connection string. Set
    /// <see cref="ConnectionString"/> before opening.
    /// </summary>
    public SqliteWasmConnection()
    {
        _bridge = SqliteWasmWorkerBridge.Instance;
    }

    /// <summary>
    /// Creates a connection for the given connection string.
    /// </summary>
    /// <param name="connectionString">
    /// Connection string naming the database in the OPFS pool, e.g.
    /// <c>Data Source=TodoDb.db</c>.
    /// </param>
    public SqliteWasmConnection(string connectionString) : this()
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override string Database => GetDatabaseName();

    /// <inheritdoc />
    public override string DataSource => GetDatabaseName();

    /// <inheritdoc />
    public override string ServerVersion => "3.47.0"; // sqlite-wasm version

    /// <inheritdoc />
    public override ConnectionState State =>
        _state == ConnectionState.Open && !_bridge.IsDatabaseOpen(Database)
            ? ConnectionState.Closed
            : _state;

    private string GetDatabaseName()
    {
        // Parse "Data Source=mydb.db" from connection string
        if (string.IsNullOrEmpty(_connectionString))
        {
            return ":memory:";
        }

        var parts = _connectionString.Split(';');
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 &&
                kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                return kv[1].Trim();
            }
        }

        return ":memory:";
    }

    /// <inheritdoc />
    public override void Open()
    {
        // EF Core's EnsureCreatedAsync may call synchronous Open() in some paths
        // We can't await in WebAssembly, but we can fire-and-forget the async operation
        if (_state == ConnectionState.Open && _bridge.IsDatabaseOpen(Database))
        {
            return;
        }

        _state = ConnectionState.Open;

        // Fire and forget - reuse OpenAsync logic
        _ = OpenAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        // Check both C# state AND worker state to detect stale connections
        // after import/export/close/delete/rename operations
        if (_state == ConnectionState.Open && _bridge.IsDatabaseOpen(Database))
        {
            return;
        }

        _state = ConnectionState.Connecting;

        try
        {
            // Single-key model: the worker uses globalKey set via
            // ISqliteWasmDatabaseService.SetEncryptionKeyAsync. Per-connection
            // EncryptionKey is no longer threaded to the bridge.
            await _bridge.OpenDatabaseAsync(Database, cancellationToken: cancellationToken);

            // PRAGMAs are set by the worker on first database open
            // This ensures they apply to the actual worker-side connection and persist
            // for the lifetime of the cached database instance

            _state = ConnectionState.Open;
        }
        catch
        {
            _state = ConnectionState.Broken;
            throw;
        }
    }

    /// <inheritdoc />
    public override void Close()
    {
        // IMPORTANT: Do NOT close the worker-side database connection here!
        //
        // The worker maintains a persistent connection pool. Opening/closing
        // the database for every DbContext operation is extremely inefficient:
        // - Each open: create SAH, set PRAGMAs, register functions
        // - Each close: flush WAL, release SAH
        //
        // Instead, we only update the C# connection state. The worker keeps
        // the database open and reuses it for subsequent operations.
        //
        // The database will only be truly closed when:
        // 1. Explicitly calling SqliteWasmWorkerBridge.CloseDatabaseAsync()
        // 2. The web worker terminates (e.g., page unload)

        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override Task CloseAsync()
    {
        // See Close() for explanation - we don't close the worker-side connection
        _state = ConnectionState.Closed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand()
    {
        return new SqliteWasmCommand
        {
            Connection = this
        };
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        throw new NotSupportedException(
            "Synchronous transactions are not supported in WebAssembly. Use BeginTransactionAsync instead.");
    }

    /// <inheritdoc />
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        if (_currentTransaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active on this connection.");
        }

        var transaction = await SqliteWasmTransaction.CreateAsync(this, isolationLevel, cancellationToken);
        _currentTransaction = transaction;
        return transaction;
    }

    internal void ClearCurrentTransaction(SqliteWasmTransaction transaction)
    {
        if (_currentTransaction == transaction)
        {
            _currentTransaction = null;
        }
    }

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("Changing database is not supported.");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }
}
