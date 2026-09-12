// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Text.Json;

namespace SqliteWasmBlazor;

// Persistence partial: single-DB import/export (opaque or plain, with REKEY/
// ENCRYPT and asymmetric verify+import variants) and bulk row import.
internal sealed partial class SqliteWasmWorkerBridge
{
    /// <summary>
    /// Write raw bytes into a pool slot, whole and from managed memory.
    ///
    /// <para>
    /// <b>Not part of the public surface.</b> Every consumer-facing import is
    /// streamed (<see cref="ImportDatabaseFromStreamAsync"/> /
    /// <see cref="ImportDatabasesFromStreamAsync"/>) and refuses anything that
    /// is not a plain SQLite file. This one accepts <em>any</em> bytes, which
    /// is what makes it the VFS test seam: writing back tampered ciphertext,
    /// or deliberate garbage, is how the encrypted-at-rest and
    /// corrupt-database behaviours are exercised. It holds the whole file in
    /// managed memory, so it is unsuitable for real data.
    /// </para>
    ///
    /// Auto-detects ciphertext vs plaintext by inspecting the first 16 bytes:
    /// if they are <c>"SQLite format 3\0"</c>, the input is treated as a plain
    /// SQLite file (normal path with byte-18 WAL-mode patch). Otherwise the
    /// input is treated as opaque ciphertext of a PRF-VFS-encrypted DB —
    /// both the header validation and the byte-18 patch are skipped because
    /// they would corrupt the AEAD tag on slot 0.
    ///
    /// Opaque imports are subject to refuse-on-existing + verify-on-write:
    /// the worker rejects writes over an existing DB at this path
    /// (<see cref="PoolImportResult.EXISTING_DB_REFUSED"/>) and, when an
    /// encryption key is registered, AEAD-tests slot 0 of the freshly written
    /// DB. A failed verify rolls back the import (unlinks the file) and
    /// returns <see cref="PoolImportResult.WRONG_KEY"/>.
    /// </summary>
    internal async Task<PoolImportResult> ImportDatabaseRawAsync(
        string databaseName,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var opaque = data.Length < 16 || !data.AsSpan(0, 16).SequenceEqual(SqliteHeaderMagic);

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
                    type = "importDb",
                    database = databaseName,
                    opaque,
                }
            });

            SendBinaryToWorker(data.AsSpan(), metadataJson);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(60000);

            SqlQueryResult result;
            try
            {
                result = await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Import database operation timed out after 60 seconds.");
            }

            // Worker closes the DB during import (no-op when the DB wasn't
            // open or when the import was refused before close).
            _openDatabases.Remove(databaseName);

            // Worker encodes the import outcome in rowsAffected (same
            // tri-state channel SetEncryptionKeyAsync uses):
            // 0 = OK, 1 = WRONG_KEY (rolled back), 2 = EXISTING_DB_REFUSED.
            return result.RowsAffected switch
            {
                0 => PoolImportResult.OK,
                1 => PoolImportResult.WRONG_KEY,
                2 => PoolImportResult.EXISTING_DB_REFUSED,
                var other => throw new InvalidOperationException(
                    $"Worker returned unexpected import outcome code {other}"),
            };
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <summary>
    /// Read a pool slot's bytes verbatim, whole and into managed memory.
    ///
    /// <para>
    /// <b>Not part of the public surface.</b> The consumer-facing exports
    /// (<see cref="ExportDatabaseToStreamAsync"/> /
    /// <see cref="ExportDatabaseToDownloadAsync"/>) emit plain pages, because
    /// a file only the pool that wrote it can read is not something to hand a
    /// user. This one emits what is physically on disk — slot-format
    /// ciphertext on an encrypted pool — which is what lets the VFS tests
    /// assert that data really is encrypted at rest, and tamper with a slot
    /// to prove AEAD catches it. It holds the whole file in managed memory,
    /// so it is unsuitable for real data.
    /// </para>
    /// </summary>
    internal Task<byte[]> ExportDatabaseRawAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
        => SendRawBinaryRequestAsync(
            databaseName,
            new { type = "exportDb", database = databaseName, mode = "verbatim" },
            "Export verbatim",
            cancellationToken);

    /// <inheritdoc />
    public async Task ExportDatabaseToStreamAsync(
        string databaseName,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException(
                "databaseName must be non-empty.", nameof(databaseName));
        }
        ArgumentNullException.ThrowIfNull(destination);

        await EnsureInitializedAsync(cancellationToken);
        ThrowIfPoolLocked(
            PoolOperationRejection.EXPORT_NEEDS_UNLOCK,
            $"ExportDatabaseToStreamAsync('{databaseName}') rejected: pool is " +
            "Encrypted+Locked. Unlock first; without the global key the worker " +
            "can't decrypt slots back to plain pages.");

        // Same worker op the download path drives, so both emit the same
        // bytes; the difference is only who drains the staging file. The
        // download hands it to the browser as a disk-backed File and leaves
        // collection to the next session's sweep — here C# reads it, so it
        // can be dropped as soon as the last slice is out.
        var staged = await SendRequestAsync(
            new { type = "exportDbToStaging", database = databaseName }, cancellationToken);
        if (string.IsNullOrEmpty(staged.StagingFile))
        {
            throw new InvalidOperationException(
                "exportDbToStaging returned no staging file name.");
        }

        // Worker closed the DB for a consistent snapshot — mirror that in
        // the C#-side open set so the next use re-opens cleanly.
        _openDatabases.Remove(databaseName);

        try
        {
            long offset = 0;
            while (offset < staged.FileSize)
            {
                var length = (int)Math.Min(ExportSliceBytes, staged.FileSize - offset);
                var slice = await SendRawBinaryRequestAsync(
                    databaseName,
                    new
                    {
                        type = "readStagingSlice",
                        name = staged.StagingFile,
                        offset,
                        length,
                    },
                    "Export to stream",
                    cancellationToken);
                if (slice.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"ExportDatabaseToStreamAsync: staging file " +
                        $"'{staged.StagingFile}' ended at {offset} of {staged.FileSize} bytes.");
                }

                await destination.WriteAsync(slice, cancellationToken);
                offset += slice.Length;
            }
        }
        finally
        {
            await SendRequestAsync(
                new { type = "deleteStagingFile", name = staged.StagingFile },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// One staging read per this many bytes. Matches the chunk the import
    /// pump pushes, so a round trip through both directions holds the same
    /// managed peak.
    /// </summary>
    private const int ExportSliceBytes = 1 << 20;

    /// <inheritdoc />
    public async Task ExportDatabaseToDownloadAsync(
        string databaseName,
        string filename,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException(
                "databaseName must be non-empty.", nameof(databaseName));
        }
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException(
                "filename must be non-empty.", nameof(filename));
        }

        await EnsureInitializedAsync(cancellationToken);
        ThrowIfPoolLocked(
            PoolOperationRejection.EXPORT_NEEDS_UNLOCK,
            $"ExportDatabaseToDownloadAsync('{databaseName}') rejected: pool is " +
            "Encrypted+Locked. Unlock first; without the global key the worker " +
            "can't decrypt slots back to plain pages.");

        var request = new { type = "exportDbToStaging", database = databaseName };
        var result = await SendRequestAsync(request, cancellationToken);
        if (string.IsNullOrEmpty(result.StagingFile))
        {
            throw new InvalidOperationException(
                "exportDbToStaging returned no staging file name.");
        }

        // Worker closed the DB for a consistent snapshot — mirror that in
        // the C#-side open set so the next use re-opens cleanly.
        _openDatabases.Remove(databaseName);

        var ok = await DownloadStagedExportAsync(result.StagingFile, filename);
        if (!ok)
        {
            throw new InvalidOperationException(
                "downloadStagedExport reported failure.");
        }
    }

    // Promoted from private → internal in plane-split Phase 1 so plane 2's
    // EncryptedSqliteWasmWorkerBridge in SqliteWasmBlazor.Crypto can drive
    // binary-payload round-trips through the same _pendingBinaryRequests map.
    internal async Task<byte[]> SendRawBinaryRequestAsync(
        string databaseName,
        object request,
        string opName,
        CancellationToken cancellationToken)
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

            var requestJson = JsonSerializer.Serialize(new { id = requestId, data = request });
            SendToWorker(requestJson);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(60000);

            try
            {
                var result = await tcs.Task.WaitAsync(timeoutCts.Token);
                // Worker closes the DB during export for consistent snapshot.
                _openDatabases.Remove(databaseName);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{opName} operation timed out after 60 seconds.");
            }
        }
        catch
        {
            _pendingBinaryRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    public async Task<int> ImportRowsAsync(
        string databaseName, byte[] data,
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
                    type = "importRows",
                    database = databaseName
                }
            });

            SendBinaryToWorker(data.AsSpan(), metadataJson);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(300_000);

            var result = await tcs.Task.WaitAsync(timeoutCts.Token);
            return result.RowsAffected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Row import timed out.");
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }
}
