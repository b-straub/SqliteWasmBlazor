// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// Set by the JSON-only response of <c>readPoolManifest</c> — one of
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
    /// <summary>
    /// Set by the JSON-only response of <c>exportDbToStaging</c> — the
    /// OPFS staging file name to hand to <c>downloadStagedExport</c>.
    /// </summary>
    public string? StagingFile { get; set; }
    /// <summary>
    /// Byte count of the staging file named by <see cref="StagingFile"/>.
    /// This is the <em>plain</em> length, which on an encrypted pool is not
    /// the source's on-disk size — a caller draining the file needs the
    /// number it will actually read.
    /// </summary>
    public long FileSize { get; set; }
}

/// <summary>
/// Bridge between C# and sqlite-wasm worker. This file owns the request /
/// response dispatcher, DB lifecycle (open/close/list/exists/delete/rename),
/// SQL execution, and the JSImport/JSExport boundary. Concern-specific
/// surface lives in sibling partials:
/// <list type="bullet">
///   <item><c>SetEncryptionKeyAsync</c> et al. — <c>.Encryption.cs</c></item>
///   <item><see cref="ImportRowsAsync"/> et al. — <c>.Persistence.cs</c></item>
///   <item><see cref="ImportDatabaseFromStreamAsync"/> et al. — <c>.Streaming.cs</c></item>
///   <item><c>DeltaExportAsync</c> et al. — <c>.Delta.cs</c></item>
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

    // ------------------------------------------------------------------
    // Request tracing (SqliteWasmOptions.EnableRequestTracing)
    //
    // Answers one question the reactive layer cannot: when a caller cancels,
    // does the work stop? It does not. Cancelling a request removes its
    // TaskCompletionSource and stops the C# await; the message is already in
    // the worker's queue and there is no cancel/interrupt message in the
    // protocol to recall it. The worker runs it to completion and its
    // response lands on an id nobody is waiting for.
    //
    // Because the worker is single-threaded, that abandoned query still holds
    // it, and the next one waits. These two dictionaries make that visible:
    // the in-flight count at send time separates "each query is slow" from
    // "queries are queueing behind abandoned ones".
    // ------------------------------------------------------------------
    // Sent to the worker, no response yet — the worker's actual backlog, which
    // survives cancellation. This is the number that matters: `_pendingRequests`
    // counts only what C# is still awaiting, and cancelling removes an entry
    // from it while the worker carries on running the request.
    //
    // Populated by the request paths that call TraceSent (the SQL paths). The
    // import/export binary paths are not traced, so a concurrent bulk transfer
    // would not be counted here.
    private readonly ConcurrentDictionary<int, long> _traceStartedAt = new();
    private readonly ConcurrentDictionary<int, long> _traceAbandonedAt = new();

    private static double ElapsedMs(long fromTimestamp) =>
        Stopwatch.GetElapsedTime(fromTimestamp).TotalMilliseconds;

    private void TraceSent(int requestId, string kind)
    {
        if (!SqliteWasmLogger.IsTracingEnabled)
        {
            return;
        }

        _traceStartedAt[requestId] = Stopwatch.GetTimestamp();

        // Both numbers include this request. When they differ, the gap is work
        // the worker is still doing for callers that have already given up.
        var outstanding = _traceStartedAt.Count;
        var awaited = _pendingRequests.Count;
        var abandoned = outstanding - awaited;

        SqliteWasmLogger.Trace(
            TraceModule,
            abandoned > 0
                ? $"req#{requestId} {kind} sent — {outstanding} outstanding at worker ({awaited} awaited, {abandoned} abandoned)"
                : $"req#{requestId} {kind} sent — {outstanding} outstanding at worker");
    }

    /// <summary>
    /// Records that the caller stopped waiting for a request the worker is
    /// still running. No-ops for an id that is no longer outstanding, so it is
    /// safe to call from a <c>finally</c> that also covers the success path.
    /// </summary>
    private void TraceAbandoned(int requestId)
    {
        if (!SqliteWasmLogger.IsTracingEnabled || !_traceStartedAt.TryGetValue(requestId, out var started))
        {
            return;
        }

        _traceAbandonedAt[requestId] = Stopwatch.GetTimestamp();
        SqliteWasmLogger.Trace(
            TraceModule,
            $"req#{requestId} abandoned by caller after {ElapsedMs(started):F0} ms — " +
            "the worker has no cancel message, so it keeps running and the next request waits behind it");
    }

    private static void TraceCompleted(int requestId)
    {
        if (!SqliteWasmLogger.IsTracingEnabled)
        {
            return;
        }

        var bridge = Instance;
        if (!bridge._traceStartedAt.TryRemove(requestId, out var started))
        {
            return;
        }

        if (bridge._traceAbandonedAt.TryRemove(requestId, out var abandoned))
        {
            SqliteWasmLogger.Trace(
                TraceModule,
                $"req#{requestId} finished anyway after {ElapsedMs(started):F0} ms " +
                $"({ElapsedMs(abandoned):F0} ms of it after it was abandoned) — result discarded");
            return;
        }

        SqliteWasmLogger.Trace(
            TraceModule,
            $"req#{requestId} completed in {ElapsedMs(started):F0} ms");
    }

    private const string TraceModule = "Bridge";
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _pendingBinaryRequests = new();
    private readonly HashSet<string> _openDatabases = new();
    private int _nextRequestId;

    /// <summary>
    /// Allocate the next worker request id. Request ids live in the
    /// positive int space; the JS bridge allocates stream ids from a
    /// NEGATIVE counter to stay collision-free — so an int overflow here
    /// (wrapping into the negative range) must fail loudly instead of
    /// silently colliding with an in-flight stream.
    /// </summary>
    private int NextRequestId()
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        if (id < 0)
        {
            throw new InvalidOperationException(
                "Worker request id space exhausted (int overflow into the " +
                "stream-id range) — reload the application.");
        }
        return id;
    }
    private bool _isInitialized;
    private volatile bool _poolLocked;
    private static TaskCompletionSource<bool>? _initializationTcs;

    /// <summary>
    /// Checks if the worker has a database open. Used by SqliteWasmConnection
    /// to detect stale C# connection state after import/export/close operations.
    /// </summary>
    internal bool IsDatabaseOpen(string database) => _openDatabases.Contains(database);

    /// <summary>
    /// True when the encrypted VFS disk is in the locked state — i.e. the
    /// disk holds ciphertext but no <c>globalKey</c> is installed. Set by
    /// <c>EncryptedSqliteWasmDatabaseService</c> at boot probe and on every Lock /
    /// Unlock / Enter / Leave / Reset transition; consulted by every
    /// DB-touching bridge method to refuse operations cleanly with
    /// <see cref="PoolLockedException"/> instead of letting them reach
    /// SQLite and surface as SQLITE_NOTADB.
    /// </summary>
    internal bool IsPoolLocked => _poolLocked;

    /// <summary>
    /// Update the disk-locked flag. Production callers always go through
    /// <c>EncryptedSqliteWasmDatabaseService</c>, which is the single source of
    /// truth for VFS state. Idempotent.
    /// </summary>
    internal void SetPoolLocked(bool locked) => _poolLocked = locked;

    /// <summary>
    /// Throw <see cref="PoolLockedException"/> if the disk is locked. Used
    /// at the top of every DB-touching bridge method as a uniform gate.
    /// </summary>
    private void ThrowIfPoolLocked(string operation)
    {
        if (_poolLocked)
        {
            throw new PoolLockedException(operation);
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

        // Hand the JS halves whatever level was set before they existed. A host
        // that calls SetLogLevel in Program.cs does so before this point, so
        // without this the worker would keep its default while the managed side
        // traced at Debug.
        SqliteWasmLogger.PublishLevel();
    }

    /// <summary>
    /// Open a database connection in the worker. Single-key model: the
    /// worker uses the global key set via <c>SetEncryptionKeyAsync</c>;
    /// open never carries a key envelope itself.
    /// </summary>
    /// <param name="database">Database file name inside the SAHPool.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task OpenDatabaseAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ThrowIfPoolLocked($"OpenDatabase('{database}')");

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
    /// <see cref="PoolLockedException"/> when the disk transitioned to
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
        ThrowIfPoolLocked($"ExecuteSql on '{database}'");

        var request = new
        {
            type = "execute",
            database,
            sql,
            parameters
        };

        // SendRequestAsync now returns SqlQueryResult directly - no deserialization needed
        return await SendRequestAsync(request, cancellationToken, "execute");
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
        ThrowIfPoolLocked($"ExecuteSqlWithBlobs on '{database}'");

        var requestId = NextRequestId();
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

            // No-ops on the success path: the response handler's TraceCompleted
            // has already retired this id. Only a request leaving here still
            // outstanding was abandoned.
            TraceAbandoned(requestId);
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
    internal async Task<SqlQueryResult> SendRequestAsync(
        object request,
        CancellationToken cancellationToken,
        string kind = "request")
    {
        var requestId = NextRequestId();
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

            TraceSent(requestId, kind);
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
            TraceAbandoned(requestId);
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
    /// (setGlobalEncryptionKey, encryptDb, writePoolManifest) where the
    /// worker reports a status/code rather than returning raw bytes.
    /// </summary>
    internal async Task<SqlQueryResult> PostBinaryAsync(
        object data,
        Memory<byte> envelope,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var requestId = NextRequestId();
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
        var requestId = NextRequestId();
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
        var requestId = NextRequestId();
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
        var requestId = NextRequestId();
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
            // Traced before correlation: a response for an abandoned request
            // still arrives here, and that arrival is the proof the worker
            // ran it to completion after the caller stopped waiting.
            // Single deserialization to typed wrapper (id + data) with custom converter
            var message = JsonSerializer.Deserialize<WorkerMessage>(messageJson, JsonOptions);

            if (message is null)
            {
                Console.Error.WriteLine("[Worker Bridge] Failed to deserialize worker message");
                return;
            }

            TraceCompleted(message.Id);

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
                    StagingFile = response.StagingFile,
                    FileSize = response.FileSize,
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
            TraceCompleted(requestId);

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


    /// <summary>
    /// Streaming whole-disk envelope export: drives a worker-side rekey
    /// loop, assembles the v3 MessagePack envelope as a virtual-concat
    /// Blob on the main thread, and triggers the download directly. C#
    /// never materialises the envelope as a managed <c>byte[]</c> — the
    /// path that breaks on iPad Safari for ~250 MB DBs.
    /// </summary>
    /// <remarks>
    /// <c>kWrap</c> uses the <c>ArraySegment&lt;byte&gt;</c> + <c>MemoryView</c>
    /// marshaling pattern rather than the <c>Span&lt;byte&gt;</c> one
    /// <see cref="SendBinaryToWorker"/> takes — <see cref="Span{T}"/> isn't
    /// supported on <c>Task</c>-returning interop (SYSLIB1072). The JS
    /// side <c>.slice()</c>s into a real <c>Uint8Array</c>; caller wipes
    /// the source buffer in <c>finally</c>.
    /// </remarks>
    [JSImport("exportPoolToDownload", "sqliteWasmWorker")]
    internal static partial Task<bool> ExportPoolToDownloadAsync(
        string filename,
        string metadataJson,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> kWrap);

    /// <summary>
    /// Test/diagnostic variant of <see cref="ExportPoolToDownloadAsync"/>:
    /// assembles the identical v3 envelope Blob, materialises it into a JS
    /// byte stash keyed by <paramref name="sessionId"/>, and resolves with
    /// the envelope length. The caller then drains the stash via
    /// <see cref="ReadExportBytes"/> and frees it via
    /// <see cref="DiscardExportBytes"/>.
    /// <para>
    /// Materialising the whole envelope on the heap is the very thing the
    /// streaming download path exists to avoid, so this is intended ONLY for
    /// in-page round-trip tests that must feed a real export back into the
    /// guided import (whose download side is unreachable from test code).
    /// <c>Task&lt;byte[]&gt;</c> isn't a supported JS-interop return shape
    /// (SYSLIB1072), hence the length + chunked-MemoryView-read protocol.
    /// See <c>EncryptedSqliteWasmDatabaseService.ExportPoolToPubkeyBytesAsync</c>.
    /// </para>
    /// </summary>
    [JSImport("exportPoolToBytesSession", "sqliteWasmWorker")]
    internal static partial Task<int> ExportPoolToBytesSessionAsync(
        string metadataJson,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> kWrap,
        int sessionId);

    /// <summary>
    /// Copies up to <c>dest.Length</c> bytes of the stashed envelope
    /// (from <see cref="ExportPoolToBytesSessionAsync"/>) starting at
    /// <paramref name="offset"/> into <paramref name="dest"/>, returning the
    /// number copied. Synchronous so the <see cref="Span{T}"/> MemoryView
    /// stays valid for the JS-side write.
    /// </summary>
    [JSImport("readExportBytes", "sqliteWasmWorker")]
    internal static partial int ReadExportBytes(
        int sessionId,
        int offset,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> dest);

    /// <summary>Frees the stashed envelope bytes for <paramref name="sessionId"/>.</summary>
    [JSImport("discardExportBytes", "sqliteWasmWorker")]
    internal static partial void DiscardExportBytes(int sessionId);

    /// <summary>
    /// Streaming disk-import — preflight. Points the worker at the envelope
    /// staged under <paramref name="sessionId"/>; it AEAD-verifies slot 0 of
    /// each file under <paramref name="kWrap"/>. Returns
    /// <c>PoolImportResult</c> (0=OK, 1=WRONG_KEY). No pool mutation either
    /// way.
    /// </summary>
    [JSImport("importPoolStreamPreflightFromSession", "sqliteWasmWorker")]
    internal static partial Task<int> ImportPoolStreamPreflightFromSessionAsync(
        int sessionId,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> kWrap);

    /// <summary>
    /// Streaming disk-import — commit. Worker re-streams the same staged
    /// envelope, decrypts each slot under <paramref name="kWrap"/>,
    /// re-encrypts under the worker's currently-registered globalKey, and
    /// writes via writeFileSlice + atomicReplaceFile. Caller MUST have run
    /// <c>WipePoolAsync</c> + <c>EnterEncryptedAsync</c> between preflight
    /// and commit.
    /// </summary>
    [JSImport("importPoolStreamCommitFromSession", "sqliteWasmWorker")]
    internal static partial Task<int> ImportPoolStreamCommitFromSessionAsync(
        int sessionId,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> kWrap);

    /// <summary>
    /// Anchor-click download of a finished OPFS staging file produced by
    /// the worker's <c>exportDbToStaging</c> request. The staging entry
    /// backs the download as a disk-backed <c>File</c>, so the bytes never
    /// enter managed or main-thread JS memory; the worker's init-time
    /// sweep collects the entry next session.
    /// </summary>
    [JSImport("downloadStagedExport", "sqliteWasmWorker")]
    internal static partial Task<bool> DownloadStagedExportAsync(
        string stagingFile,
        string filename);

    /// <summary>
    /// Streaming multi-DB plain export — emits a <c>.dbs</c> envelope
    /// (MessagePack array of <c>[name, bytes]</c> tuples; no compression).
    /// State-aware on the worker side: Plain emits verbatim; Encrypted+
    /// Unlocked decrypts each file slot-by-slot to plain pages.
    /// <paramref name="dbNamesJson"/> is a JSON-encoded array of names
    /// (C#'s <see cref="System.Text.Json.JsonSerializer"/> doesn't marshal
    /// <see cref="IReadOnlyList{T}"/> across the JSImport boundary, so we
    /// serialise to a string at the C# call site).
    /// </summary>
    [JSImport("exportDatabasesToDownload", "sqliteWasmWorker")]
    private static partial Task<bool> ExportDatabasesToDownloadJsAsync(
        string filename,
        string dbNamesJson);

    /// <summary>
    /// Streaming multi-DB plain import — consumes the <c>.dbs</c> envelope
    /// the C# side already pushed into the worker's import session. Worker
    /// parses the MessagePack array and writes each entry through the
    /// chunked SAH path (Plain: verbatim; Encrypted+Unlocked: rekey-on-write
    /// under globalKey). Caller refuses Encrypted+Locked.
    /// </summary>
    /// <param name="sessionId">Import session holding the staged envelope.</param>
    /// <param name="keepExisting">
    /// <c>false</c> wipes the pool before writing — the replace-the-pool
    /// contract. <c>true</c> leaves it alone because the caller has parked
    /// the previous content itself and will restore or drop it once the
    /// import has been inspected.
    /// </param>
    [JSImport("importDatabasesFromSession", "sqliteWasmWorker")]
    private static partial Task<int> ImportDatabasesFromSessionAsync(
        int sessionId,
        bool keepExisting);
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
    /// Set by <c>readPoolManifest</c> — see <see cref="SqlQueryResult.ManifestState"/>.
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
    /// <summary>
    /// Set by <c>exportDbToStaging</c>, see <see cref="SqlQueryResult.StagingFile"/>.
    /// </summary>
    public string? StagingFile { get; set; }
    /// <summary>
    /// Set by <c>exportDbToStaging</c>, see <see cref="SqlQueryResult.FileSize"/>.
    /// </summary>
    public long FileSize { get; set; }
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
