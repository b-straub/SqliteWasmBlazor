// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace SqliteWasmBlazor;

/// <summary>
/// Result from SQL query execution in worker.
/// Deserialized using MessagePack typeless mode due to dynamic object?[][] data.
/// </summary>
internal sealed class SqlQueryResult
{
    public List<string> ColumnNames { get; set; } = [];
    public List<string> ColumnTypes { get; set; } = [];
    public object?[][] Rows { get; set; } = [];
    public int RowsAffected { get; set; }
    public long LastInsertId { get; set; }
}

/// <summary>
/// Bridge between C# and sqlite-wasm worker.
/// Handles message passing and response coordination.
/// </summary>
internal sealed partial class SqliteWasmWorkerBridge : ISqliteWasmDatabaseService
{
    // ReSharper disable once InconsistentNaming
    private static readonly Lazy<SqliteWasmWorkerBridge> _instance = new(() => new SqliteWasmWorkerBridge());
    public static SqliteWasmWorkerBridge Instance => _instance.Value;

    /// <summary>
    /// First 16 bytes of every valid SQLite database file.
    /// </summary>
    private static ReadOnlySpan<byte> SqliteHeaderMagic => "SQLite format 3\0"u8;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<SqlQueryResult>> _pendingRequests = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _pendingBinaryRequests = new();
    private readonly HashSet<string> _openDatabases = new();
    private int _nextRequestId;
    private bool _isInitialized;
    private static TaskCompletionSource<bool>? _initializationTcs;

    /// <summary>
    /// Checks if the worker has a database open. Used by SqliteWasmConnection
    /// to detect stale C# connection state after import/export/close operations.
    /// </summary>
    internal bool IsDatabaseOpen(string database) => _openDatabases.Contains(database);

    // ReSharper disable once InconsistentNaming
    private static readonly Lazy<JsonSerializerOptions> _jsonOptions = new(() =>
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = WorkerJsonContext.Default
        };
        return options;
    });

    private static JsonSerializerOptions JsonOptions => _jsonOptions.Value;

    // ReSharper disable once InconsistentNaming
    private static readonly Lazy<MessagePackSerializerOptions> _messagePackOptions = new(() =>
        MessagePackSerializerOptions.Standard
            .WithResolver(TypelessContractlessStandardResolver.Instance));

    private static MessagePackSerializerOptions MessagePackOptions => _messagePackOptions.Value;

    private SqliteWasmWorkerBridge()
    {
    }

    /// <summary>
    /// Initialize the worker bridge. Invoked from <see cref="SqliteWasmServiceCollectionExtensions.InitializeSqliteWasmAsync"/>
    /// with options resolved from DI — callers should not invoke this directly.
    /// </summary>
    /// <param name="options">Resolved <see cref="SqliteWasmOptions"/> carrying <c>BaseHref</c> and <c>AssetRoot</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(SqliteWasmOptions options, CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        var bridgePath = $"{options.BaseHref}{options.AssetRoot}sqlite-wasm-bridge.js";

        await JSHost.ImportAsync("sqliteWasmWorker", bridgePath, cancellationToken);

        _initializationTcs = new TaskCompletionSource<bool>();
        var token = cancellationToken;
        await using var registration = token.Register(() => _initializationTcs.TrySetCanceled());

        // Single awaitable JSImport: creates the Worker and posts the init message.
        // CSP-safe (no DOM read, no data: URLs); worker ready/error signal arrives via JSExport.
        await InitializeBridgeAsync(options.BaseHref, options.AssetRoot);

        var ready = await _initializationTcs.Task;
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
            type = "open", database
        };

        await SendRequestAsync(request, cancellationToken);
        _openDatabases.Add(database);
    }

    /// <summary>
    /// Close a database connection in the worker, releasing the OPFS SAH.
    /// </summary>
    public async Task CloseDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            return; // Worker not initialized, nothing to close
        }

        var request = new
        {
            type = "close", database = databaseName
        };

        await SendRequestAsync(request, cancellationToken);
        _openDatabases.Remove(databaseName);
    }

    /// <summary>
    /// Execute SQL in the worker and return results.
    /// </summary>
    public async Task<SqlQueryResult> ExecuteSqlAsync(
        string database,
        string sql,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var request = new
        {
            type = "execute",
            database,
            sql,
            parameters
        };

        // SendRequestAsync now returns SqlQueryResult directly - no deserialization needed
        return await SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Check if a database exists in OPFS SAHPool storage.
    /// </summary>
    public async Task<bool> ExistsDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var request = new
        {
            type = "exists", database = databaseName
        };

        var result = await SendRequestAsync(request, cancellationToken);

        // Worker returns exists: true/false in the response
        return result.RowsAffected > 0;
    }

    /// <summary>
    /// Delete a database from OPFS SAHPool storage.
    /// </summary>
    public async Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var request = new
        {
            type = "delete", database = databaseName
        };

        await SendRequestAsync(request, cancellationToken);
        _openDatabases.Remove(databaseName);
    }

    /// <summary>
    /// Rename a database in OPFS SAHPool storage (atomic operation).
    /// </summary>
    public async Task RenameDatabaseAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var request = new
        {
            type = "rename",
            database = oldName,
            newName
        };

        await SendRequestAsync(request, cancellationToken);
        _openDatabases.Remove(oldName);
    }

    /// <summary>
    /// Import a raw .db file into OPFS SAHPool storage.
    /// Validates SQLite header before sending to worker.
    /// </summary>
    public async Task ImportDatabaseAsync(string databaseName, byte[] data, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        // Validate SQLite header
        if (data.Length < 16 || !data.AsSpan(0, 16).SequenceEqual(SqliteHeaderMagic))
        {
            throw new ArgumentException("Data is not a valid SQLite database file (invalid header).", nameof(data));
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>();

        _pendingRequests[requestId] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
            {
                _pendingRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });

            var metadataJson = JsonSerializer.Serialize(new
            {
                id = requestId,
                data = new { type = "importDb", database = databaseName }
            });

            SendBinaryToWorker(data.AsSpan(), metadataJson);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(60000);

            try
            {
                await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Import database operation timed out after 60 seconds.");
            }

            // Worker closes the DB during import
            _openDatabases.Remove(databaseName);
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <summary>
    /// Export a raw .db file from OPFS SAHPool storage.
    /// Database is closed before export for a consistent snapshot.
    /// </summary>
    public async Task<byte[]> ExportDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<byte[]>();

        _pendingBinaryRequests[requestId] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
            {
                _pendingBinaryRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });

            var requestJson = JsonSerializer.Serialize(new
            {
                id = requestId,
                data = new { type = "exportDb", database = databaseName }
            });

            SendToWorker(requestJson);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(60000);

            try
            {
                var result = await tcs.Task.WaitAsync(timeoutCts.Token);
                // Worker closes the DB during export for consistent snapshot
                _openDatabases.Remove(databaseName);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Export database operation timed out after 60 seconds.");
            }
        }
        catch
        {
            _pendingBinaryRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <summary>
    /// Bulk import: sends V2 MessagePack payload to worker for direct prepared statement insertion.
    /// One worker round-trip per call instead of ~80 ExecuteSqlRawAsync calls.
    /// </summary>
    public async Task<int> BulkImportAsync(string databaseName, byte[] payload, ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.None,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>();

        _pendingRequests[requestId] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
            {
                _pendingRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });

            var metadataJson = JsonSerializer.Serialize(new
            {
                id = requestId,
                data = new { type = "bulkImport", database = databaseName, conflictStrategy = (int)conflictStrategy }
            });

            SendBinaryToWorker(payload.AsSpan(), metadataJson);

            // 5-minute timeout for bulk operations
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(300_000);

            try
            {
                var result = await tcs.Task.WaitAsync(timeoutCts.Token);
                return result.RowsAffected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Bulk import operation timed out after 5 minutes.");
            }
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <summary>
    /// Bulk import from raw MessagePack row data (no V2 header needed).
    /// Metadata sent as JSON, row data as binary payload.
    /// </summary>
    public async Task<int> BulkImportRawAsync(string databaseName, BulkImportMetadata metadata, byte[] rowData,
        ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.None,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>();

        _pendingRequests[requestId] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
            {
                _pendingRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });

            var metadataJson = JsonSerializer.Serialize(new
            {
                id = requestId,
                data = new
                {
                    type = "bulkImportRaw",
                    database = databaseName,
                    conflictStrategy = (int)conflictStrategy,
                    tableName = metadata.TableName,
                    columns = metadata.Columns,
                    primaryKeyColumn = metadata.PrimaryKeyColumn
                }
            });

            SendBinaryToWorker(rowData.AsSpan(), metadataJson);

            // 5-minute timeout for bulk operations
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(300_000);

            try
            {
                var result = await tcs.Task.WaitAsync(timeoutCts.Token);
                return result.RowsAffected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Bulk import operation timed out after 5 minutes.");
            }
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <summary>
    /// Bulk export: worker queries SQLite directly and returns V2 MessagePack bytes.
    /// </summary>
    public async Task<byte[]> BulkExportAsync(string databaseName, BulkExportMetadata exportMetadata, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<byte[]>();

        _pendingBinaryRequests[requestId] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
            {
                _pendingBinaryRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });

            // Merge exportMetadata into the request data alongside type and database
            var dataDict = JsonSerializer.SerializeToNode(exportMetadata, JsonOptions)?.AsObject()
                ?? new System.Text.Json.Nodes.JsonObject();
            dataDict["type"] = "bulkExport";
            dataDict["database"] = databaseName;

            var requestJson = JsonSerializer.Serialize(new
            {
                id = requestId,
                data = dataDict
            });

            SendToWorker(requestJson);

            // 5-minute timeout for bulk operations
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(300_000);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Bulk export operation timed out after 5 minutes.");
            }
        }
        catch
        {
            _pendingBinaryRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            return Task.CompletedTask;
        }

        // Fail fast rather than silently lazy-initialising with defaults —
        // on a sub-path or browser-extension build the defaults ("/" + "_content/SqliteWasmBlazor/")
        // would 404 the worker and produce a confusing timeout. Forcing explicit init
        // at the DI layer makes the misconfiguration a visible startup error.
        throw new InvalidOperationException(
            "SqliteWasm is not initialized. Call services.InitializeSqliteWasmAsync() " +
            "or services.InitializeSqliteWasmDatabaseAsync<TContext>() in Program.cs before performing any database operation.");
    }

    private async Task<SqlQueryResult> SendRequestAsync(object request, CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>();

        _pendingRequests[requestId] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
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

            // Timeout for general SQL operations.
            // Must be long enough for heavy operations like FTS5 rebuild on large databases.
#if DEBUG
            const int defaultTimeoutMs = 300_000; // 5 minutes in debug
#else
            const int defaultTimeoutMs = 300_000; // 5 minutes in release
#endif
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(defaultTimeoutMs);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout occurred (not user cancellation)
                throw new TimeoutException($"Database operation timed out after {defaultTimeoutMs / 1000} seconds.");
            }
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
            // Single deserialization to typed wrapper (id + data) with custom converter
            var message = JsonSerializer.Deserialize<WorkerMessage>(messageJson, JsonOptions);

            if (message is null)
            {
                Console.Error.WriteLine("[Worker Bridge] Failed to deserialize worker message");
                return;
            }

            var response = message.Data;

            // Check for error response — route to either pending requests or pending binary requests
            if (!response.Success)
            {
                if (Instance._pendingRequests.TryRemove(message.Id, out var errorTcs))
                {
                    errorTcs.TrySetException(new InvalidOperationException($"Worker error: {response.Error ?? "Unknown error"}"));
                }
                else if (Instance._pendingBinaryRequests.TryRemove(message.Id, out var binaryErrorTcs))
                {
                    binaryErrorTcs.TrySetException(new InvalidOperationException($"Worker error: {response.Error ?? "Unknown error"}"));
                }

                return;
            }

            if (Instance._pendingRequests.TryRemove(message.Id, out var tcs))
            {
                // Create SqlQueryResult for non-execute operations (open, close, exists)
                var result = new SqlQueryResult
                {
                    ColumnNames = response.ColumnNames ?? [],
                    ColumnTypes = response.ColumnTypes ?? [],
                    Rows = [],
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

    /// <summary>
    /// Callback for binary MessagePack responses from worker (execute operations).
    /// Uint8Array is marshalled to byte array for MessagePack deserialization.
    /// Uses typeless deserialization due to dynamic object?[][] data types.
    /// </summary>
    [JSExport]
    public static void OnWorkerResponseBinary(int requestId, byte[] messageData)
    {
        try
        {
            // Deserialize MessagePack binary data
            var responseObj = MessagePackSerializer.Typeless.Deserialize(messageData, MessagePackOptions);

            if (responseObj is null)
            {
                Console.Error.WriteLine("[Worker Bridge] Failed to deserialize MessagePack data");
                return;
            }

            // MessagePack typeless API returns Dictionary<object, object>
            if (responseObj is not Dictionary<object, object> responseDict)
            {
                Console.Error.WriteLine($"[Worker Bridge] Unexpected response type: {responseObj.GetType().FullName}");
                if (Instance._pendingRequests.TryRemove(requestId, out var errorTcs))
                {
                    errorTcs.TrySetException(new InvalidCastException($"Expected Dictionary<object, object> but got {responseObj.GetType().FullName}"));
                }
                return;
            }

            // Extract fields with type conversions
            var columnNames = responseDict.TryGetValue("columnNames", out var cnValue)
                ? ((object[])cnValue).Cast<string>().ToList()
                : [];

            var columnTypes = responseDict.TryGetValue("columnTypes", out var ctValue)
                ? ((object[])ctValue).Cast<string>().ToList()
                : [];

            // Extract typed rows data
            object?[][] rows = [];
            if (responseDict.TryGetValue("typedRows", out var trValue))
            {
                var typedRowsDict = (Dictionary<object, object>)trValue;
                if (typedRowsDict.TryGetValue("data", out var dataValue))
                {
                    var dataArray = (object[])dataValue;
                    rows = dataArray
                        .Select(rowObj => ((object[])rowObj)
                            .Select(ConvertMessagePackValue)
                            .ToArray())
                        .ToArray();
                }
            }

            var rowsAffected = responseDict.TryGetValue("rowsAffected", out var raValue)
                ? ConvertToInt32(raValue)
                : 0;

            var lastInsertId = responseDict.TryGetValue("lastInsertId", out var liiValue)
                ? ConvertToInt64(liiValue)
                : 0L;

            // Complete the pending request
            if (Instance._pendingRequests.TryRemove(requestId, out var tcs))
            {
                var result = new SqlQueryResult
                {
                    ColumnNames = columnNames,
                    ColumnTypes = columnTypes,
                    Rows = rows,
                    RowsAffected = rowsAffected,
                    LastInsertId = lastInsertId
                };

                tcs.TrySetResult(result);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Worker Bridge] MessagePack deserialization failed: {ex}");
            if (Instance._pendingRequests.TryRemove(requestId, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// Convert MessagePack deserialized values to expected C# types.
    /// </summary>
    private static object? ConvertMessagePackValue(object? value)
    {
        return value switch
        {
            null => null,
            byte[] bytes => bytes,           // BLOB (stays binary!)
            string s => s,                   // TEXT
            bool b => b ? 1L : 0L,           // BOOLEAN → INTEGER (SQLite stores as 0/1)
            long l => l,                     // INTEGER
            int i => (long)i,                // INTEGER (ensure long)
            double d => d,                   // REAL
            float f => (double)f,            // REAL (ensure double)
            byte by => (long)by,             // BYTE → INTEGER
            short sh => (long)sh,            // SHORT → INTEGER
            _ => value.ToString()            // Fallback to string
        };
    }

    /// <summary>
    /// Safely convert MessagePack numeric value to Int32.
    /// </summary>
    private static int ConvertToInt32(object? value)
    {
        return value switch
        {
            int i => i,
            long l => (int)l,
            float f => (int)f,
            double d => (int)d,
            byte b => b,
            short s => s,
            _ => 0
        };
    }

    /// <summary>
    /// Safely convert MessagePack numeric value to Int64.
    /// </summary>
    private static long ConvertToInt64(object? value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            float f => (long)f,
            double d => (long)d,
            byte b => b,
            short s => s,
            _ => 0L
        };
    }

    /// <summary>
    /// Callback for raw binary responses from worker (export operations).
    /// Uint8Array is marshalled to byte array.
    /// </summary>
    [JSExport]
    public static void OnWorkerResponseRawBinary(int requestId, byte[] data)
    {
        try
        {
            if (Instance._pendingBinaryRequests.TryRemove(requestId, out var tcs))
            {
                tcs.TrySetResult(data);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Worker Bridge] Raw binary response processing failed: {ex}");
            if (Instance._pendingBinaryRequests.TryRemove(requestId, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// Called from JavaScript when worker signals ready.
    /// </summary>
    [JSExport]
    public static void OnWorkerReady()
    {
        _initializationTcs?.TrySetResult(true);
    }

    /// <summary>
    /// Called from JavaScript when worker initialization fails.
    /// </summary>
    [JSExport]
    public static void OnWorkerError(string error)
    {
        _initializationTcs?.TrySetException(new InvalidOperationException($"Worker initialization failed: {error}"));
    }

    [JSImport("initializeBridge", "sqliteWasmWorker")]
    private static partial Task InitializeBridgeAsync(string baseHref, string assetRoot);

    [JSImport("sendToWorker", "sqliteWasmWorker")]
    private static partial void SendToWorker(string messageJson);

    [JSImport("sendBinaryToWorker", "sqliteWasmWorker")]
    private static partial void SendBinaryToWorker([JSMarshalAs<JSType.MemoryView>] Span<byte> data, string metadataJson);
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
/// Used only for JSON error messages - execute responses use MessagePack binary format.
/// </summary>
internal sealed class WorkerResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<string>? ColumnNames { get; set; }
    public List<string>? ColumnTypes { get; set; }
    public int RowsAffected { get; set; }
    public long LastInsertId { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for efficient, zero-allocation serialization.
/// Uses Web defaults for camelCase and other web-friendly settings.
/// Used only for error messages - execute responses use MessagePack binary format.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WorkerMessage))]
[JsonSerializable(typeof(WorkerResponse))]
[JsonSerializable(typeof(SqlQueryResult))]
[JsonSerializable(typeof(BulkExportMetadata))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}
