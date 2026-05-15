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
    /// <summary>
    /// Set by the JSON-only response of <c>listDatabases</c>.
    /// </summary>
    public List<string>? Databases { get; set; }
    /// <summary>
    /// Set by the JSON-only response of <c>readDiskManifest</c> — one of
    /// "absent" / "present" / "mismatch" / "tampered" / "malformed".
    /// </summary>
    public string? ManifestState { get; set; }
    /// <summary>
    /// Base64 of the manifest body bytes, populated when
    /// <see cref="ManifestState"/> is "present".
    /// </summary>
    public string? ManifestBody { get; set; }
    /// <summary>
    /// Schema version byte (0x01 = v1) of a present manifest.
    /// </summary>
    public int? ManifestSchemaVersion { get; set; }
}

/// <summary>
/// Bridge between C# and sqlite-wasm worker. This file owns the request /
/// response dispatcher, DB lifecycle (open/close/list/exists/delete/rename),
/// SQL execution, and the JSImport/JSExport boundary. Concern-specific
/// surface lives in sibling partials:
/// <list type="bullet">
///   <item><see cref="SetEncryptionKeyAsync"/> et al. — <c>.Encryption.cs</c></item>
///   <item><see cref="ImportDatabaseAsync"/> et al. — <c>.Persistence.cs</c></item>
///   <item><see cref="DeltaExportAsync"/> et al. — <c>.Delta.cs</c></item>
/// </list>
/// </summary>
internal sealed partial class SqliteWasmWorkerBridge : ISqliteWasmDatabaseService
{
    // ReSharper disable once InconsistentNaming
    private static readonly Lazy<SqliteWasmWorkerBridge> _instance = new(() => new SqliteWasmWorkerBridge());
    public static SqliteWasmWorkerBridge Instance => _instance.Value;

    /// <summary>
    /// First 16 bytes of every valid SQLite database file. Internal so
    /// SqliteWasmBlazor.Crypto can run the same plain-vs-opaque probe at
    /// the disk-level import entry points without re-deriving the magic.
    /// </summary>
    internal static ReadOnlySpan<byte> SqliteHeaderMagic => "SQLite format 3\0"u8;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<SqlQueryResult>> _pendingRequests = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _pendingBinaryRequests = new();
    private readonly HashSet<string> _openDatabases = new();
    private int _nextRequestId;
    private bool _isInitialized;
    private volatile bool _diskLocked;
    private static TaskCompletionSource<bool>? _initializationTcs;

    /// <summary>
    /// Checks if the worker has a database open. Used by SqliteWasmConnection
    /// to detect stale C# connection state after import/export/close operations.
    /// </summary>
    internal bool IsDatabaseOpen(string database) => _openDatabases.Contains(database);

    /// <summary>
    /// True when the encrypted VFS disk is in the locked state — i.e. the
    /// disk holds ciphertext but no <c>globalKey</c> is installed. Set by
    /// <see cref="EncryptedSqliteWasmDatabaseService"/> at boot probe and on every Lock /
    /// Unlock / Enter / Leave / Reset transition; consulted by every
    /// DB-touching bridge method to refuse operations cleanly with
    /// <see cref="DiskLockedException"/> instead of letting them reach
    /// SQLite and surface as SQLITE_NOTADB.
    /// </summary>
    internal bool IsDiskLocked => _diskLocked;

    /// <summary>
    /// Update the disk-locked flag. Production callers always go through
    /// <see cref="EncryptedSqliteWasmDatabaseService"/>, which is the single source of
    /// truth for VFS state. Idempotent.
    /// </summary>
    internal void SetDiskLocked(bool locked) => _diskLocked = locked;

    /// <summary>
    /// Throw <see cref="DiskLockedException"/> if the disk is locked. Used
    /// at the top of every DB-touching bridge method as a uniform gate.
    /// </summary>
    private void ThrowIfDiskLocked(string operation)
    {
        if (_diskLocked)
        {
            throw new DiskLockedException(operation);
        }
    }

    // ReSharper disable once InconsistentNaming
    private static readonly Lazy<JsonSerializerOptions> _jsonOptions = new(() =>
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = WorkerJsonContext.Default
        };
        return options;
    });

    // Promoted from private → internal in plane-split Phase 2b so plane 2's
    // EncryptedSqliteWasmWorkerBridge can use the same JsonSerializerOptions
    // (source-generated context) when assembling its delta-export metadata.
    internal static JsonSerializerOptions JsonOptions => _jsonOptions.Value;

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
    /// Open a database connection in the worker. Single-key model: the
    /// worker uses the global key set via <see cref="SetEncryptionKeyAsync"/>;
    /// open never carries a key envelope itself.
    /// </summary>
    /// <param name="database">Database file name inside the SAHPool.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task OpenDatabaseAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ThrowIfDiskLocked($"OpenDatabase('{database}')");

        // ALWAYS reach the worker. The worker's `openDatabase` is
        // idempotent (it short-circuits on its own openDatabases Map). The
        // bridge-side `_openDatabases` mirror is just an optimization, and
        // it can drift with the worker if a prior `Session.LockAsync`
        // closed a SqliteWasmConnection's underlying DB without the C# side
        // knowing. Treating the mirror as authoritative would silently send
        // `executeSql` to a worker DB that's already gone — surfacing as
        // "Database X not open".
        var request = new { type = "open", database };
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
    /// Close every DB the bridge tracks as open via the public
    /// <see cref="CloseDatabaseAsync"/>, which keeps the
    /// <see cref="_openDatabases"/> mirror in sync. Called by Set/Clear
    /// EncryptionKey to mirror the worker's cache-coherence close pass.
    /// </summary>
    // Promoted from private → internal in plane-split Phase 2b so plane 2's
    // EncryptedSqliteWasmWorkerBridge can run the close-pass before installing
    // a new global key (page-cache coherence at session boundary).
    internal async Task CloseAllOpenDatabasesAsync(CancellationToken cancellationToken)
    {
        // Snapshot first — CloseDatabaseAsync mutates _openDatabases.
        string[] snapshot;
        lock (_openDatabases)
        {
            snapshot = _openDatabases.Count == 0 ? [] : [.. _openDatabases];
        }
        foreach (var db in snapshot)
        {
            try { await CloseDatabaseAsync(db, cancellationToken); }
            catch { /* best-effort; caller is establishing a clean slate */ }
        }
    }

    /// <summary>
    /// Bare main-DB names currently in OPFS — no journal/WAL siblings.
    /// Used by <c>EncryptedSqliteWasmDatabaseService.EnterEncryptedAsync</c> /
    /// <c>LeaveEncryptedAsync</c> to iterate every DB for the all-or-
    /// nothing transition.
    /// </summary>
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var request = new { type = "listDatabases" };
        var result = await SendRequestAsync(request, cancellationToken);
        return result.Databases ?? [];
    }

    /// <summary>
    /// Execute SQL in the worker and return results. Throws
    /// <see cref="DiskLockedException"/> when the disk transitioned to
    /// locked between connection open and query (e.g. the user explicitly
    /// hit Lock while a DbContext was alive).
    /// </summary>
    public async Task<SqlQueryResult> ExecuteSqlAsync(
        string database,
        string sql,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        ThrowIfDiskLocked($"ExecuteSql on '{database}'");

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
    /// Execute SQL with blob parameters delivered via the binary-send path
    /// instead of Base64-stringified into the JSON message. Param-dict entries
    /// for blobs carry <c>{ __blobOffset, __blobLength }</c> placeholders
    /// pointing into <paramref name="packedBlobs"/>; the worker reads bytes
    /// from the binary attachment at the listed offsets. Eliminates the
    /// per-blob Base64 alloc + ~3 MB-of-allocation JSON-marshal chain for
    /// blob writes.
    /// </summary>
    internal async Task<SqlQueryResult> ExecuteSqlWithBlobsAsync(
        string database,
        string sql,
        Dictionary<string, object?> parameters,
        byte[] packedBlobs,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        ThrowIfDiskLocked($"ExecuteSqlWithBlobs on '{database}'");

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    type = "execute",
                    database,
                    sql,
                    parameters,
                }
            });

            SendBinaryToWorker(packedBlobs.AsSpan(), metadataJson);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(60000);
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"ExecuteSqlWithBlobsAsync on '{database}' timed out");
            }
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
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

    // Promoted from private → internal in plane-split Phase 1 so the future
    // EncryptedSqliteWasmWorkerBridge in SqliteWasmBlazor.Crypto can drive
    // request/response round-trips through the same TaskCompletionSource map.
    // No behavior change: same-assembly partials (.Encryption.cs / .Delta.cs)
    // continue to see this method exactly as before.
    internal async Task<SqlQueryResult> SendRequestAsync(object request, CancellationToken cancellationToken)
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

    // ============================================================================
    // Binary-payload helpers — plane-split Phase 2 seam
    // ============================================================================
    //
    // These four helpers encapsulate the "alloc request id + post envelope to
    // worker + correlate response via pending map + clean up" dance currently
    // duplicated across the bridge's encryption / delta partials. Added here
    // so Phase 2b's EncryptedSqliteWasmWorkerBridge (in SqliteWasmBlazor.Crypto)
    // can drive every binary worker round-trip without poking the bridge's
    // private state directly.
    //
    // Caller still owns:
    //   - building the request metadata (the `data` object, opaque here)
    //   - serializing payload(s) to byte[]/ReadOnlyMemory<byte>
    //   - any caller-specific cleanup (ZeroMemory, VfsKeyHeader.Clear) — done
    //     in the caller's own finally block around the helper call

    /// <summary>
    /// Post a binary envelope + JSON metadata to the worker, correlate the
    /// response as <see cref="SqlQueryResult"/>. Used for encryption ops
    /// (setGlobalEncryptionKey, encryptDb, writeDiskManifest) where the
    /// worker reports a status/code rather than returning raw bytes.
    /// </summary>
    internal async Task<SqlQueryResult> PostBinaryAsync(
        object data,
        Memory<byte> envelope,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>();
        _pendingRequests[requestId] = tcs;

        try
        {
            var metadataJson = JsonSerializer.Serialize(new { id = requestId, data });
            SendBinaryToWorker(envelope.Span, metadataJson);

            await using var registration = cancellationToken.Register(() =>
            {
                _pendingRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });
            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Variant of <see cref="PostBinaryAsync"/> that ships a second binary
    /// buffer alongside the main envelope. Used by encrypted bulk-rotate-key
    /// (two CryptoHeaders) and any future op needing dual binary inputs.
    /// </summary>
    internal async Task<SqlQueryResult> PostBinaryWithHeaderAsync(
        object data,
        Memory<byte> envelope,
        Memory<byte> header,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<SqlQueryResult>();
        _pendingRequests[requestId] = tcs;

        try
        {
            var metadataJson = JsonSerializer.Serialize(new { id = requestId, data });
            SendBinaryToWorkerWithHeader(envelope.Span, metadataJson, header.Span);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t)
            {
                timeoutCts.CancelAfter(t);
            }

            await using var registration = timeoutCts.Token.Register(() =>
            {
                _pendingRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Post a binary envelope + JSON metadata to the worker, expect a binary
    /// (byte[]) response. Used for delta export and mode-aware DB export
    /// where the worker streams back serialized data.
    /// </summary>
    internal async Task<byte[]> PostBinaryForBytesAsync(
        object data,
        Memory<byte> envelope,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<byte[]>();
        _pendingBinaryRequests[requestId] = tcs;

        try
        {
            var metadataJson = JsonSerializer.Serialize(new { id = requestId, data });
            SendBinaryToWorker(envelope.Span, metadataJson);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t)
            {
                timeoutCts.CancelAfter(t);
            }

            await using var registration = timeoutCts.Token.Register(() =>
            {
                _pendingBinaryRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            _pendingBinaryRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Variant of <see cref="PostBinaryForBytesAsync"/> with a second binary
    /// buffer (envelope + header). Used by encrypted delta import which ships
    /// both the CryptoHeader and the DeltaEnvelope.
    /// </summary>
    internal async Task<byte[]> PostBinaryWithHeaderForBytesAsync(
        object data,
        Memory<byte> envelope,
        Memory<byte> header,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<byte[]>();
        _pendingBinaryRequests[requestId] = tcs;

        try
        {
            var metadataJson = JsonSerializer.Serialize(new { id = requestId, data });
            SendBinaryToWorkerWithHeader(envelope.Span, metadataJson, header.Span);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t)
            {
                timeoutCts.CancelAfter(t);
            }

            await using var registration = timeoutCts.Token.Register(() =>
            {
                _pendingBinaryRequests.TryRemove(requestId, out _);
                tcs.TrySetCanceled();
            });
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            _pendingBinaryRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Drop a database from the bridge's open-database mirror. Called by
    /// encryption / delta ops where the worker autonomously closes the DB
    /// during an in-place conversion (encrypt / decrypt / rekey-export) so
    /// the next DbContext open re-enters xOpen.
    /// </summary>
    internal void MarkDatabaseClosed(string databaseName) => _openDatabases.Remove(databaseName);

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
                    LastInsertId = response.LastInsertId,
                    Databases = response.Databases,
                    ManifestState = response.ManifestState,
                    ManifestBody = response.ManifestBody,
                    ManifestSchemaVersion = response.ManifestSchemaVersion,
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
            // Deserialize MessagePack binary data. byte[] marshaling is the
            // runtime's supported path for JS-originated buffers crossing
            // into a JSExport — [JSMarshalAs<MemoryView>] ArraySegment<byte>
            // asserts at runtime ("Only roundtrip of ArraySegment instance
            // created by C#") because MemoryView marshal requires the
            // ArraySegment to have originated in C#. A pinned-managed-byte[]
            // + round-trip MemoryView pattern would in principle eliminate
            // the per-call managed allocation (single memcpy via
            // MemoryView.set on the JS side, buffer reused), and a custom
            // Module._malloc/_free + IntPtr+Span<byte> on JSExport would
            // be even leaner — but both depend on runtime-API surface that
            // is in flux pending the Mono → NativeAOT-on-browser transition.
            // Decision 2026-05-11: stay on the supported byte[] path until
            // the .NET maintainers settle the heap-API direction.
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

    [JSImport("sendBinaryToWorker", "sqliteWasmWorker")]
    private static partial void SendBinaryToWorkerWithHeader(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> data,
        string metadataJson,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> header);
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
    /// <summary>
    /// Returned by the <c>listDatabases</c> worker op — bare main-DB names
    /// in the SAHPool (no journal/WAL siblings).
    /// </summary>
    public List<string>? Databases { get; set; }
    /// <summary>
    /// Set by <c>readDiskManifest</c> — see <see cref="SqlQueryResult.ManifestState"/>.
    /// </summary>
    public string? ManifestState { get; set; }
    /// <summary>
    /// Base64 manifest body, see <see cref="SqlQueryResult.ManifestBody"/>.
    /// </summary>
    public string? ManifestBody { get; set; }
    /// <summary>
    /// Schema version, see <see cref="SqlQueryResult.ManifestSchemaVersion"/>.
    /// </summary>
    public int? ManifestSchemaVersion { get; set; }
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
[JsonSerializable(typeof(TableExportSpec))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}
