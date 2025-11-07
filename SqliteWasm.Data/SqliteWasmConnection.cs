// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// Minimal SQLite connection for EF Core using sqlite-wasm + OPFS.
/// </summary>
public sealed class SqliteWasmConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private ConnectionState _state = ConnectionState.Closed;
    private readonly SqliteWasmWorkerBridge _bridge;
    private SqliteWasmTransaction? _currentTransaction;

    public SqliteWasmConnection()
    {
        _bridge = SqliteWasmWorkerBridge.Instance;
    }

    public SqliteWasmConnection(string connectionString) : this()
    {
        _connectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? string.Empty;
    }

    public override string Database => GetDatabaseName();

    public override string DataSource => GetDatabaseName();

    public override string ServerVersion => "3.47.0"; // sqlite-wasm version

    public override ConnectionState State => _state;

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

    public override void Open()
    {
        // EF Core's EnsureCreatedAsync may call synchronous Open() in some paths
        // We can't await in WebAssembly, but we can fire-and-forget the async operation
        if (_state == ConnectionState.Open)
        {
            return;
        }

        _state = ConnectionState.Open;

        // Fire and forget - reuse OpenAsync logic
        _ = OpenAsync(CancellationToken.None);
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
        {
            return;
        }

        _state = ConnectionState.Connecting;

        try
        {
            await _bridge.OpenDatabaseAsync(Database, cancellationToken);

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

    public override void Close()
    {
        // Cannot call async operation from sync method in WebAssembly
        // Fire and forget close operation - worker will clean up SAH
        if (_state == ConnectionState.Open)
        {
            _ = CloseAsync();
        }
        _state = ConnectionState.Closed;
    }

    public override async Task CloseAsync()
    {
        if (_state != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await _bridge.CloseDatabaseAsync(Database);
            _state = ConnectionState.Closed;
        }
        catch
        {
            _state = ConnectionState.Broken;
            throw;
        }
    }

    protected override DbCommand CreateDbCommand()
    {
        return new SqliteWasmCommand
        {
            Connection = this
        };
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        throw new NotSupportedException(
            "Synchronous transactions are not supported in WebAssembly. Use BeginTransactionAsync instead.");
    }

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

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("Changing database is not supported.");
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
