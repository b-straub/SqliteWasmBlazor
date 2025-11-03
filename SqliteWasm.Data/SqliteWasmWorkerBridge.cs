// System.Data.SQLite.Wasm - Minimal EF Core compatible provider
// MIT License

using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// Result from SQL query execution in worker.
/// </summary>
public sealed class SqlQueryResult
{
    public List<string> ColumnNames { get; set; } = new();
    public List<string> ColumnTypes { get; set; } = new();
    public List<List<object?>> Rows { get; set; } = new();
    public int RowsAffected { get; set; }
    public long LastInsertId { get; set; }
}

/// <summary>
/// Bridge between C# and sqlite-wasm worker.
/// Handles message passing and response coordination.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class SqliteWasmWorkerBridge
{
    private static readonly Lazy<SqliteWasmWorkerBridge> _instance = new(() => new SqliteWasmWorkerBridge());
    public static SqliteWasmWorkerBridge Instance => _instance.Value;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<SqlQueryResult>> _pendingRequests = new();
    private int _nextRequestId;
    private bool _isInitialized;

    private SqliteWasmWorkerBridge()
    {
    }

    /// <summary>
    /// Initialize the worker and sqlite-wasm module.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        await JSHost.ImportAsync("sqliteWasmWorker", "/_content/System.Data.SQLite.Wasm/sqlite-wasm-bridge.js", cancellationToken);

        // Wait for worker to signal ready
        var tcs = new TaskCompletionSource<bool>();
        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        SetReadyCallback(() => tcs.TrySetResult(true));

        var ready = await tcs.Task;
        if (!ready)
        {
            throw new InvalidOperationException("Worker failed to initialize.");
        }

        _isInitialized = true;
    }

    /// <summary>
    /// Open a database connection in the worker.
    /// </summary>
    public async Task OpenDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var request = new
        {
            type = "open",
            database = database
        };

        await SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Execute SQL in the worker and return results.
    /// </summary>
    public async Task<SqlQueryResult> ExecuteSqlAsync(
        string database,
        string sql,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var request = new
        {
            type = "execute",
            database = database,
            sql = sql,
            parameters = parameters
        };

        // SendRequestAsync now returns SqlQueryResult directly - no deserialization needed
        return await SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Delete a database from OPFS SAHPool storage.
    /// </summary>
    public async Task DeleteDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var request = new
        {
            type = "delete",
            database = database
        };

        await SendRequestAsync(request, cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    private async Task<SqlQueryResult> SendRequestAsync(object request, CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>();

        _pendingRequests[requestId] = tcs;

        try
        {
            using var registration = cancellationToken.Register(() =>
            {
                _pendingRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });

            var requestJson = JsonSerializer.Serialize(new
            {
                id = requestId,
                data = request
            });

            SendToWorker(requestJson);

            return await tcs.Task;
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <summary>
    /// Called from JavaScript when worker responds.
    /// Receives JSON string and deserializes with source-generated context.
    /// Single deserialization eliminates overhead of parsing twice.
    /// </summary>
    [JSExport]
    public static void OnWorkerResponse(string messageJson)
    {
        try
        {
            // Single deserialization to typed wrapper (id + data)
            var message = JsonSerializer.Deserialize(messageJson, WorkerJsonContext.Default.WorkerMessage);

            if (message is null)
            {
                Console.Error.WriteLine("[Worker Bridge] Failed to deserialize worker message");
                return;
            }

            if (Instance._pendingRequests.TryRemove(message.Id, out var tcs))
            {
                var response = message.Data;

                // Check for error response
                if (!response.Success)
                {
                    tcs.TrySetException(new InvalidOperationException($"Worker error: {response.Error ?? "Unknown error"}"));
                    return;
                }

                // Create SqlQueryResult directly - no re-serialization
                var result = new SqlQueryResult
                {
                    ColumnNames = response.ColumnNames ?? new List<string>(),
                    ColumnTypes = response.ColumnTypes ?? new List<string>(),
                    Rows = response.Rows ?? new List<List<object?>>(),
                    RowsAffected = response.RowsAffected,
                    LastInsertId = response.LastInsertId
                };

                tcs.TrySetResult(result);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Worker Bridge] Error processing worker response: {ex.Message}");
        }
    }

    [JSImport("sendToWorker", "sqliteWasmWorker")]
    private static partial void SendToWorker(string messageJson);

    [JSImport("setReadyCallback", "sqliteWasmWorker")]
    private static partial void SetReadyCallback([JSMarshalAs<JSType.Function>] Action callback);
}

/// <summary>
/// Worker message wrapper (includes id + data).
/// </summary>
internal sealed class WorkerMessage
{
    public int Id { get; set; }
    public WorkerResponse Data { get; set; } = new();
}

/// <summary>
/// Worker response structure (matches JavaScript response format).
/// </summary>
internal sealed class WorkerResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<string>? ColumnNames { get; set; }
    public List<string>? ColumnTypes { get; set; }
    public List<List<object?>>? Rows { get; set; }
    public int RowsAffected { get; set; }
    public long LastInsertId { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for efficient, zero-allocation serialization.
/// Uses Web defaults for camelCase and other web-friendly settings.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WorkerMessage))]
[JsonSerializable(typeof(WorkerResponse))]
[JsonSerializable(typeof(SqlQueryResult))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}
