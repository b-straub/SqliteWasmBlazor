// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Buffers;

namespace SqliteWasmBlazor;

// Streaming partial: the memory-flat file paths. Every method here moves a
// database (or a pool's worth of them) between a Stream and the SAH pool
// without either heap holding more than one ArrayPool chunk — the chunks go
// into the worker one at a time and are written straight into pool slots
// there. Nothing assembles the file, which is what keeps mobile Safari alive
// on large transfers.
//
// The worker behind these is state-aware on its own: the Crypto plane's
// bundle rekeys on the way in and decrypts on the way out, this plane's
// writes and reads plain pages. What this plane owns is the refusal when the
// pool is locked, the park/restore bookkeeping around a validated import,
// and the chunk pump.
internal sealed partial class SqliteWasmWorkerBridge
{
    /// <summary>
    /// Monotonic import-session id allocator. Independent of the request-id
    /// counter; only needs to be unique among the worker's open import
    /// sessions for the duration of one streaming import.
    /// </summary>
    private int _nextSessionId;

    private IDbInitializationReporter? _bootReporter;
    private IDbInitializationStatus? _bootStatus;
    private Func<IHostDatabaseService?>? _resolveHost;

    /// <summary>
    /// Attach the boot-status surface a whole-pool import reports through.
    /// Called by the library's init helpers, which resolve both facets from
    /// DI at app start — the bridge is a singleton constructed outside the
    /// container and cannot resolve them itself.
    /// </summary>
    internal void AttachBootStatus(
        IDbInitializationReporter reporter, IDbInitializationStatus status)
    {
        _bootReporter = reporter;
        _bootStatus = status;
    }

    /// <summary>
    /// Attach the host seam the import paths reconcile against. A
    /// <em>resolver</em> rather than an instance so that "not registered" is
    /// a null at the point of use rather than a failure at attach time.
    /// Absent — no registration, or a host that never initialized — imports
    /// simply skip the step.
    /// </summary>
    internal void AttachHostDatabaseService(Func<IHostDatabaseService?> resolve)
    {
        _resolveHost = resolve;
    }

    /// <summary>
    /// The host seam, or <c>null</c> when none is registered. Internal so
    /// the Crypto plane's guided <c>.eds</c> import reconciles through the
    /// same attach point rather than resolving its own.
    /// </summary>
    internal IHostDatabaseService? HostDatabaseService => _resolveHost?.Invoke();

    /// <summary>
    /// Reconcile the host's schema with what an import just landed. Every
    /// import ends here, which is what makes the invariant hold for a
    /// headless consumer and not only for the drop-in UI: the bytes may
    /// carry an older schema than the app's model, and an owned database
    /// the source omitted has to be re-created before the next query hits
    /// it.
    /// </summary>
    private async Task MigrateAfterImportAsync(CancellationToken cancellationToken)
    {
        var host = HostDatabaseService;
        if (host is null)
        {
            return;
        }

        await host.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Report a lifecycle state, so every <c>&lt;AuthorizeView&gt;</c> bound
    /// to it re-evaluates without a manual UI poke. A hard-stop boot
    /// diagnosis (TAB_LOCKED, SCHEMA_INCOMPATIBLE, FAILED, TIMEOUT) needs
    /// user action beyond an import and is never overwritten from here.
    /// </summary>
    private void ReportDbState(DbInitState state)
    {
        if (_bootStatus?.State is DbInitState.TAB_LOCKED
            or DbInitState.SCHEMA_INCOMPATIBLE
            or DbInitState.TIMEOUT
            or DbInitState.FAILED)
        {
            return;
        }

        _bootReporter?.Report(state);
    }

    /// <summary>
    /// Allocate the next import-session id. Same overflow contract as the
    /// request ids: session ids must stay positive (the JS side uses
    /// negative ids for streams), so wraparound fails loudly.
    /// </summary>
    internal int NextSessionId()
    {
        var id = Interlocked.Increment(ref _nextSessionId);
        if (id < 0)
        {
            throw new InvalidOperationException(
                "Import session id space exhausted (int overflow) — reload the application.");
        }

        return id;
    }

    /// <summary>
    /// Refuse a file operation the worker cannot carry out without the
    /// global key. Locked means the pool holds ciphertext and no key is
    /// installed: an export has nothing to turn slots back into plain pages
    /// with, and an import has nothing to encrypt what lands. Both refuse
    /// here rather than surfacing a worker slot-size error.
    /// </summary>
    private void ThrowIfPoolLocked(PoolOperationRejection reason, string message)
    {
        if (_poolLocked)
        {
            throw new PoolOperationRejectedException(reason, message);
        }
    }

    // -----------------------------------------------------------------
    // Import-session primitives — the chunk pump's worker-facing half.
    // Internal: production callers go through the streaming import methods
    // below, and the Crypto plane's guided .eds import drives the same
    // session from its own two-pass flow.
    // -----------------------------------------------------------------

    /// <summary>
    /// Open an import session in the worker. The picked file is pushed in
    /// chunk by chunk from there — nothing about it is ever assembled on
    /// the main thread, which is what a Blob part list did and what WebKit
    /// holds in process memory until the pool's access handles die under it.
    ///
    /// <para>
    /// <paramref name="sink"/> says where the chunks land.
    /// <c>"database"</c> writes them straight into the pool's temp slot for
    /// <paramref name="databaseName"/> (rekeyed on the way in when the pool
    /// is encrypted), which <see cref="ImportSessionCloseAsync"/> promotes —
    /// one pass, no copy of the file anywhere.
    /// <c>"staging"</c> writes them into an OPFS staging file the worker
    /// re-streams per pass, for the envelope imports that validate in one
    /// pass and commit in another.
    /// </para>
    /// </summary>
    /// <param name="sessionId">Caller-allocated id; must be unique and closed by Discard.</param>
    /// <param name="sink"><c>"database"</c> or <c>"staging"</c>.</param>
    /// <param name="databaseName">Target database, for a database sink.</param>
    /// <param name="size">Declared source length in bytes, for a database sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task ImportSessionOpenAsync(
        int sessionId,
        string sink,
        string? databaseName,
        long size,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            type = "importSessionOpen", sessionId, sink, database = databaseName, size,
        };
        await SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Push one chunk into an open import session. The buffer is transferred
    /// to the worker, so the main thread keeps nothing; awaiting the call is
    /// the flow control — the next chunk is read only once this one is on
    /// disk.
    /// </summary>
    internal async Task ImportSessionAppendAsync(
        int sessionId,
        Memory<byte> chunk,
        CancellationToken cancellationToken = default)
    {
        await PostBinaryAsync(
            new { type = "importSessionAppend", sessionId }, chunk, cancellationToken);
    }

    /// <summary>
    /// End the source. A database session commits here — its temp slot is
    /// promoted over the database. A staging session only closes the write
    /// side; what happens to the envelope is the pass that reads it back.
    /// </summary>
    internal async Task ImportSessionCloseAsync(
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new { type = "importSessionClose", sessionId };
        await SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Drop the session and anything it staged. Idempotent — call it from a
    /// finally-block whether the import committed, failed, or never started.
    /// </summary>
    internal async Task ImportSessionDiscardAsync(
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new { type = "importSessionDiscard", sessionId };
        await SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Put <paramref name="sourceName"/> in <paramref name="targetName"/>'s
    /// place — the target's pool slot is freed and the source's slot takes
    /// over its name, in one metadata update. No bytes are copied and no
    /// intermediate state exists: either the source is the target now, or
    /// nothing moved.
    ///
    /// <para>
    /// This is the park/restore primitive.
    /// <see cref="ISqliteWasmDatabaseService.RenameDatabaseAsync"/> cannot
    /// stand in for it: renaming onto a name the pool already holds leaves
    /// the occupant's slot claimed but unreachable, and splitting the job
    /// into delete-then-rename leaves a park and the import that displaced
    /// it standing side by side if the pool fails in between — with nothing
    /// left to say which of the two is the database.
    /// </para>
    /// </summary>
    /// <param name="sourceName">Entry that takes over the target's name.</param>
    /// <param name="targetName">Name to place it under; its content is dropped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task ReplaceDatabaseAsync(
        string sourceName,
        string targetName,
        CancellationToken cancellationToken = default)
    {
        var request = new { type = "replaceDb", database = sourceName, targetName };
        await SendRequestAsync(request, cancellationToken);
        // The worker closes both to swap the slots; keep the C# mirror in step.
        MarkDatabaseClosed(sourceName);
        MarkDatabaseClosed(targetName);
    }

    // -----------------------------------------------------------------
    // The streamed file surface.
    // -----------------------------------------------------------------

    /// <inheritdoc />
    public async Task ExportDatabasesToDownloadAsync(
        IReadOnlyList<string> databaseNames,
        string filename,
        CancellationToken cancellationToken = default)
    {
        if (databaseNames is null || databaseNames.Count == 0)
        {
            throw new ArgumentException(
                "databaseNames must be non-empty.", nameof(databaseNames));
        }

        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException(
                "filename must be non-empty.", nameof(filename));
        }

        await EnsureInitializedAsync(cancellationToken);
        ThrowIfPoolLocked(
            PoolOperationRejection.EXPORT_NEEDS_UNLOCK,
            "ExportDatabasesToDownloadAsync rejected: pool is Encrypted+Locked. " +
            "Unlock first; without the global key the worker can't decrypt slots " +
            "back to plain pages.");

        if (databaseNames.Count == 1)
        {
            await ExportDatabaseToDownloadAsync(
                databaseNames[0], filename, cancellationToken);
            return;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(databaseNames);
        var ok = await ExportDatabasesToDownloadJsAsync(filename, json);
        if (!ok)
        {
            throw new InvalidOperationException(
                "ExportDatabasesToDownloadAsync: bridge reported failure.");
        }
    }

    /// <inheritdoc />
    public async Task ImportDatabaseFromStreamAsync(
        string databaseName,
        Stream stream,
        long size,
        Func<string, CancellationToken, ValueTask>? validateImported = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException(
                "databaseName must be non-empty.", nameof(databaseName));
        }

        if (size <= 0)
        {
            throw new ArgumentException(
                $"size must be positive, got {size}.", nameof(size));
        }

        await EnsureInitializedAsync(cancellationToken);
        ThrowIfPoolLocked(
            PoolOperationRejection.PLAIN_IMPORT_NEEDS_UNLOCK,
            "ImportDatabaseFromStreamAsync rejected: pool is Encrypted+Locked. " +
            "Unlock first, or use the .eds guided import to rebind the pool to " +
            "a different credential.");

        // atomicReplaceFile frees the target's current SAH at commit time;
        // an open handle would keep serving its pages. The worker closes the
        // DB itself — this keeps the C# open-set mirror in step.
        await CloseDatabaseAsync(databaseName, cancellationToken);

        if (validateImported is null)
        {
            await StreamIntoDatabaseAsync(databaseName, stream, size, cancellationToken);
            await MigrateAfterImportAsync(cancellationToken);
            return;
        }

        // Validated import. The file is written under the database's real
        // name — page AAD binds ciphertext to the database path, so an
        // import written under any other name would stop decrypting the
        // moment it was moved there. What was in the way is parked instead,
        // and the parked bytes go back untouched if the validator refuses.
        await SweepImportParksAsync(cancellationToken);
        var parked = PoolNaming.ImportParkFor(databaseName);
        var replaced = (await ListDatabasesAsync(cancellationToken))
            .Contains(databaseName, StringComparer.Ordinal);
        if (replaced)
        {
            await ReplaceDatabaseAsync(databaseName, parked, cancellationToken);
        }

        try
        {
            await StreamIntoDatabaseAsync(databaseName, stream, size, cancellationToken);
            await validateImported(databaseName, cancellationToken);
        }
        catch (Exception importFailure)
        {
            var rollbackFailure = await UndoImportAsync(
                databaseName, replaced, cancellationToken);
            if (rollbackFailure is not null)
            {
                throw RollbackFailed(
                    $"Import of '{databaseName}'", importFailure, rollbackFailure);
            }

            throw;
        }

        if (replaced)
        {
            await DeleteDatabaseAsync(parked, cancellationToken);
        }

        await MigrateAfterImportAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ImportDatabasesFromStreamAsync(
        Stream envelopeStream,
        long envelopeSize,
        Func<string, CancellationToken, ValueTask>? validateImported = null,
        CancellationToken cancellationToken = default)
    {
        if (envelopeSize <= 0)
        {
            throw new ArgumentException(
                $"envelopeSize must be positive, got {envelopeSize}.",
                nameof(envelopeSize));
        }

        await EnsureInitializedAsync(cancellationToken);
        ThrowIfPoolLocked(
            PoolOperationRejection.PLAIN_IMPORT_NEEDS_UNLOCK,
            "ImportDatabasesFromStreamAsync rejected: pool is Encrypted+Locked. " +
            "Unlock first, or use the .eds guided import to rebind the pool " +
            "to a different credential.");

        // The commit pass replaces pool files; an OFile that outlives its
        // SAH keeps writing into a freed slot. The worker closes its own
        // cache independently — this pre-pass keeps the C# mirror in sync.
        await CloseAllOpenDatabasesAsync(cancellationToken);

        if (validateImported is null)
        {
            await StreamIntoPoolAsync(
                envelopeStream, envelopeSize, keepExisting: false, cancellationToken);
            await MigrateAfterImportAsync(cancellationToken);
            ReportDbState(DbInitState.READY);
            return;
        }

        await SweepImportParksAsync(cancellationToken);
        // A park the sweep left standing belongs to a database that is
        // present; it is not itself one, and parking it would produce a
        // park of a park. This import's own parking pass replaces it.
        var replaced = (await ListDatabasesAsync(cancellationToken))
            .Where(name => !PoolNaming.IsImportPark(name))
            .ToArray();
        foreach (var name in replaced)
        {
            await ReplaceDatabaseAsync(
                name, PoolNaming.ImportParkFor(name), cancellationToken);
        }

        IReadOnlyList<string> imported = [];
        try
        {
            await StreamIntoPoolAsync(
                envelopeStream, envelopeSize, keepExisting: true, cancellationToken);
            // Everything unparked is what the envelope brought — the pool
            // was emptied of its own names by the parking pass above.
            imported =
            [
                .. (await ListDatabasesAsync(cancellationToken))
                .Where(name => !PoolNaming.IsImportPark(name))
            ];
            foreach (var name in imported)
            {
                await validateImported(name, cancellationToken);
            }
        }
        catch (Exception importFailure)
        {
            var rollbackFailure = await UndoPoolImportAsync(
                replaced, imported, cancellationToken);
            if (rollbackFailure is not null)
            {
                throw RollbackFailed(
                    "Import of the database envelope", importFailure, rollbackFailure);
            }

            throw;
        }

        // Accepted: the parked content is what the import replaces.
        foreach (var name in replaced)
        {
            await DeleteDatabaseAsync(
                PoolNaming.ImportParkFor(name), cancellationToken);
        }

        await MigrateAfterImportAsync(cancellationToken);
        ReportDbState(DbInitState.READY);
    }

    /// <summary>
    /// Ship <paramref name="stream"/> into <paramref name="targetName"/> one
    /// ArrayPool chunk at a time through a worker import session. The worker
    /// writes a temp SAH slot and promotes it on close, so a mid-stream
    /// failure leaves <paramref name="targetName"/> untouched.
    /// </summary>
    private async Task StreamIntoDatabaseAsync(
        string targetName,
        Stream stream,
        long size,
        CancellationToken cancellationToken)
    {
        var sessionId = NextSessionId();
        await ImportSessionOpenAsync(
            sessionId, "database", targetName, size, cancellationToken);
        try
        {
            await PumpIntoImportSessionAsync(
                sessionId, stream, size, nameof(ImportDatabaseFromStreamAsync),
                cancellationToken);
            // Commits the temp slot over targetName. Everything the worker
            // could refuse — short source, wrong page shape, not a SQLite
            // file — it refused while the chunks were arriving.
            await ImportSessionCloseAsync(sessionId, cancellationToken);
        }
        finally
        {
            await ImportSessionDiscardAsync(sessionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Ship a <c>.dbs</c> envelope into the worker one ArrayPool chunk at a
    /// time. <paramref name="keepExisting"/> is passed through to the
    /// worker's commit pass: <c>false</c> wipes the pool first,
    /// <c>true</c> leaves it to the caller's park/restore bookkeeping.
    /// </summary>
    private async Task StreamIntoPoolAsync(
        Stream envelopeStream,
        long envelopeSize,
        bool keepExisting,
        CancellationToken cancellationToken)
    {
        var sessionId = NextSessionId();
        await ImportSessionOpenAsync(
            sessionId, "staging", null, envelopeSize, cancellationToken);
        try
        {
            await PumpIntoImportSessionAsync(
                sessionId, envelopeStream, envelopeSize,
                nameof(ImportDatabasesFromStreamAsync), cancellationToken);
            await ImportSessionCloseAsync(sessionId, cancellationToken);

            var result = await ImportDatabasesFromSessionAsync(sessionId, keepExisting);
            if (result != (int)PoolImportResult.OK)
            {
                throw new InvalidOperationException(
                    $"ImportDatabasesFromStreamAsync: worker returned result={result}.");
            }
        }
        finally
        {
            await ImportSessionDiscardAsync(sessionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Feed <paramref name="stream"/> into an open worker import session,
    /// one ArrayPool chunk at a time. Each append is awaited, so exactly one
    /// chunk is in flight and neither heap holds more than that — the whole
    /// reason the import goes through the worker rather than a main-thread
    /// Blob.
    /// </summary>
    internal async Task PumpIntoImportSessionAsync(
        int sessionId,
        Stream stream,
        long size,
        string what,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 1 << 20;
        var buf = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            long totalRead = 0;
            while (totalRead < size)
            {
                var read = await stream.ReadAsync(
                    buf.AsMemory(0, chunkSize), cancellationToken);
                if (read <= 0)
                {
                    throw new InvalidOperationException(
                        $"{what}: stream ended at {totalRead} of {size} bytes; " +
                        $"source is truncated.");
                }

                totalRead += read;
                await ImportSessionAppendAsync(
                    sessionId, buf.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf, clearArray: true);
        }
    }

    /// <summary>
    /// Settle parks a previous import left behind, so this import's own
    /// parking pass starts from a clean pool. A park outlives its import
    /// when the tab dies mid-flight or the pool's access handles are closed
    /// under the rollback.
    ///
    /// <para>
    /// A park whose database is absent is the only copy of that database —
    /// the restore that would have put it back never ran — so it goes back
    /// under its own name. A park whose database is present is left alone:
    /// the names cannot say whether it outlived a finished import or a
    /// rollback that got half-way, and this import's own parking pass
    /// replaces it in a moment either way. Nothing here deletes a park.
    /// </para>
    /// </summary>
    private async Task SweepImportParksAsync(CancellationToken cancellationToken)
    {
        var pool = await ListDatabasesAsync(cancellationToken);
        var present = new HashSet<string>(
            pool.Where(name => !PoolNaming.IsImportPark(name)), StringComparer.Ordinal);
        foreach (var name in pool)
        {
            if (!PoolNaming.IsImportPark(name))
            {
                continue;
            }

            var parkedFor = PoolNaming.DatabaseNameForPark(name);
            if (present.Add(parkedFor))
            {
                await ReplaceDatabaseAsync(name, parkedFor, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Undo a refused import of a single database: the park goes back over
    /// what arrived in one replace, or — when the import was creating the
    /// database rather than replacing one — what arrived is dropped.
    /// Returns the failure that stopped the rollback, or <c>null</c> when
    /// the pool is back to how it was.
    /// </summary>
    private async Task<Exception?> UndoImportAsync(
        string databaseName, bool replaced, CancellationToken cancellationToken)
    {
        try
        {
            if (replaced)
            {
                await ReplaceDatabaseAsync(
                    PoolNaming.ImportParkFor(databaseName), databaseName, cancellationToken);
            }
            else
            {
                await DeleteDatabaseAsync(databaseName, cancellationToken);
            }
            return null;
        }
        catch (Exception rollbackFailure)
        {
            return rollbackFailure;
        }
    }

    /// <summary>
    /// Undo a refused whole-envelope import: everything the envelope brought
    /// that did not displace a database of its own is dropped, and every
    /// park goes back over what took its name. The replaces are
    /// metadata-only, so what comes back is byte-identical to what was
    /// parked, page AAD included.
    ///
    /// <para>
    /// Every entry is attempted even after one fails — a database that can
    /// still be put back should be, whatever happened to the one before it.
    /// The failures travel back together; <c>null</c> means the pool is back
    /// to how it was.
    /// </para>
    /// </summary>
    private async Task<Exception?> UndoPoolImportAsync(
        IReadOnlyList<string> parked,
        IReadOnlyList<string> imported,
        CancellationToken cancellationToken)
    {
        var parkedNames = new HashSet<string>(parked, StringComparer.Ordinal);
        List<Exception>? failures = null;
        foreach (var name in imported.Where(name => !parkedNames.Contains(name)))
        {
            try
            {
                await DeleteDatabaseAsync(name, cancellationToken);
            }
            catch (Exception dropFailure)
            {
                (failures ??= []).Add(dropFailure);
            }
        }

        foreach (var name in parked)
        {
            try
            {
                await ReplaceDatabaseAsync(
                    PoolNaming.ImportParkFor(name), name, cancellationToken);
            }
            catch (Exception restoreFailure)
            {
                (failures ??= []).Add(restoreFailure);
            }
        }

        return failures is null ? null : new AggregateException(failures);
    }

    /// <summary>
    /// The exception a refused import propagates when its rollback could
    /// not finish either — both failures, and where the data is.
    /// </summary>
    private static AggregateException RollbackFailed(
        string what, Exception importFailure, Exception rollbackFailure) =>
        new($"{what} was refused and the previous content could not be put back. " +
            $"It is parked under the \"{PoolNaming.ImportParkSuffix}\" suffix and goes " +
            $"back under its own name when this app next starts.",
            importFailure, rollbackFailure);
}
