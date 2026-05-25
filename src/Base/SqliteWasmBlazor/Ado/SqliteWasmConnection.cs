// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace SqliteWasmBlazor;

/// <summary>
/// Minimal SQLite connection for EF Core using sqlite-wasm + OPFS.
/// </summary>
public sealed class SqliteWasmConnection : DbConnection
{
    private static readonly object PendingTransactionCleanupGate = new();
    private static readonly Dictionary<string, Task> PendingTransactionCleanupByDatabase = new(StringComparer.Ordinal);
    private static readonly object DatabaseTransactionGateLock = new();
    private static readonly Dictionary<string, SemaphoreSlim> DatabaseTransactionGates = new(StringComparer.Ordinal);

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

    public SqliteWasmConnection(string connectionString, LogLevel logLevel = LogLevel.Warning) : this(connectionString)
    {
        // Set log level before any worker operations
        if (OperatingSystem.IsBrowser())
        {
            SqliteWasmLogger.SetLogLevel(logLevel);
        }
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

    public override ConnectionState State =>
        _state == ConnectionState.Open && !_bridge.IsDatabaseOpen(Database)
            ? ConnectionState.Closed
            : _state;

    private string GetDatabaseName()
    {
        return GetDatabaseName(_connectionString);
    }

    internal static string GetDatabaseName(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return ":memory:";
        }

        // Parse common SQLite connection string keys:
        // "Data Source=my.db", "DataSource=my.db", or "Filename=my.db".
        foreach (var part in SplitConnectionString(connectionString))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 &&
                IsDataSourceKey(kv[0].Trim()))
            {
                return UnquoteConnectionStringValue(kv[1].Trim());
            }
        }

        return ":memory:";
    }

    private static IEnumerable<string> SplitConnectionString(string connectionString)
    {
        var start = 0;
        var quote = '\0';

        for (var i = 0; i < connectionString.Length; i++)
        {
            var ch = connectionString[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == ';')
            {
                yield return connectionString[start..i];
                start = i + 1;
            }
        }

        yield return connectionString[start..];
    }

    private static bool IsDataSourceKey(string key)
    {
        return key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Filename", StringComparison.OrdinalIgnoreCase);
    }

    private static string UnquoteConnectionStringValue(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

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

    public override Task CloseAsync()
    {
        // See Close() for explanation - we don't close the worker-side connection
        _state = ConnectionState.Closed;
        return Task.CompletedTask;
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
        await WaitForPendingTransactionCleanupAsync(cancellationToken).ConfigureAwait(false);

        if (_currentTransaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active on this connection.");
        }

        var transactionLease = await EnterDatabaseTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await SqliteWasmTransaction.CreateAsync(this, isolationLevel, transactionLease, cancellationToken);
            _currentTransaction = transaction;
            return transaction;
        }
        catch
        {
            transactionLease.Dispose();
            throw;
        }
    }

    internal void ClearCurrentTransaction(SqliteWasmTransaction transaction)
    {
        if (_currentTransaction == transaction)
        {
            _currentTransaction = null;
        }
    }

    internal void TrackPendingTransactionCleanup(Task cleanupTask)
    {
        if (cleanupTask.IsCompletedSuccessfully)
        {
            return;
        }

        lock (PendingTransactionCleanupGate)
        {
            if (PendingTransactionCleanupByDatabase.TryGetValue(Database, out var pending) && !pending.IsCompleted)
            {
                PendingTransactionCleanupByDatabase[Database] = Task.WhenAll(pending, cleanupTask);
            }
            else
            {
                PendingTransactionCleanupByDatabase[Database] = cleanupTask;
            }
        }
    }

    internal async Task WaitForPendingTransactionCleanupAsync(CancellationToken cancellationToken = default)
    {
        Task? pending;
        lock (PendingTransactionCleanupGate)
        {
            PendingTransactionCleanupByDatabase.TryGetValue(Database, out pending);
        }

        if (pending is null || pending.IsCompletedSuccessfully)
        {
            return;
        }

        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task WaitForDatabaseTransactionAccessAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is not null)
        {
            return Task.CompletedTask;
        }

        return WaitForDatabaseTransactionAccessCoreAsync(cancellationToken);
    }

    private async Task<IDisposable> EnterDatabaseTransactionAsync(CancellationToken cancellationToken)
    {
        var gate = GetDatabaseTransactionGate(Database);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseTransactionScope(gate);
    }

    private async Task WaitForDatabaseTransactionAccessCoreAsync(CancellationToken cancellationToken)
    {
        var gate = GetDatabaseTransactionGate(Database);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gate.Release();
    }

    private static SemaphoreSlim GetDatabaseTransactionGate(string database)
    {
        lock (DatabaseTransactionGateLock)
        {
            if (!DatabaseTransactionGates.TryGetValue(database, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                DatabaseTransactionGates[database] = gate;
            }

            return gate;
        }
    }

    private sealed class DatabaseTransactionScope : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _disposed;

        public DatabaseTransactionScope(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gate.Release();
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
