// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Security.Cryptography;
using System.Text.Json;
using MessagePack;

namespace SqliteWasmBlazor;

// Persistence partial: single-DB import/export (opaque or plain, with REKEY/
// ENCRYPT and asymmetric verify+import variants), ZIP-bundled multi-DB
// import/export, and bulk row import.
internal sealed partial class SqliteWasmWorkerBridge
{
    /// <summary>
    /// Import a raw .db file into OPFS SAHPool storage.
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
    /// (<see cref="DiskImportResult.EXISTING_DB_REFUSED"/>) and, when an
    /// encryption key is registered, AEAD-tests slot 0 of the freshly written
    /// DB. A failed verify rolls back the import (unlinks the file) and
    /// returns <see cref="DiskImportResult.WRONG_KEY"/>.
    /// </summary>
    public async Task<DiskImportResult> ImportDatabaseAsync(
        string databaseName,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var opaque = data.Length < 16 || !data.AsSpan(0, 16).SequenceEqual(SqliteHeaderMagic);

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
                0 => DiskImportResult.OK,
                1 => DiskImportResult.WRONG_KEY,
                2 => DiskImportResult.EXISTING_DB_REFUSED,
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

    /// <inheritdoc />
    public Task<byte[]> ExportDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
        => SendRawBinaryRequestAsync(
            databaseName,
            new { type = "exportDb", database = databaseName, mode = "verbatim" },
            "Export verbatim",
            cancellationToken);

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
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <inheritdoc />
    public async Task<byte[]> ExportAllDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        // Loop ListDatabasesAsync → ExportDatabaseAsync(name) (VERBATIM)
        // and pack into a ZIP archive with one .db entry per DB. Standard
        // cross-tool format; recipient unzips and opens each .db in any
        // SQLite tool. For whole-disk encrypted backup, use
        // IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAsync
        // (asymmetric MessagePack envelope of slot-format ciphertext).
        var names = await ListDatabasesAsync(cancellationToken);
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names)
            {
                var bytes = await ExportDatabaseAsync(name, cancellationToken);
                var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(bytes, cancellationToken);
            }
        }
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task<DiskImportResult> ImportAllDatabasesAsync(
        byte[] zipBytes,
        CancellationToken cancellationToken = default)
    {
        if (zipBytes is null || zipBytes.Length == 0)
        {
            throw new ArgumentException(
                "ImportAllDatabasesAsync: zipBytes must be a non-empty ZIP archive.",
                nameof(zipBytes));
        }

        // Replace-all semantics: wipe the pool first, then unpack each ZIP
        // entry. Caller is responsible for explicit user confirmation in UI.
        var existing = await ListDatabasesAsync(cancellationToken);
        foreach (var name in existing)
        {
            await DeleteDatabaseAsync(name, cancellationToken);
        }

        using var ms = new MemoryStream(zipBytes);
        using var zip = new System.IO.Compression.ZipArchive(
            ms, System.IO.Compression.ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // skip directory entries
            }
            using var entryMs = new MemoryStream(checked((int)entry.Length));
            await using (var entryStream = entry.Open())
            {
                await entryStream.CopyToAsync(entryMs, cancellationToken);
            }
            var bytes = entryMs.ToArray();
            var result = await ImportDatabaseAsync(entry.Name, bytes, cancellationToken);
            if (result != DiskImportResult.OK)
            {
                return result;
            }
        }
        return DiskImportResult.OK;
    }

    public async Task<int> ImportRowsAsync(
        string databaseName, byte[] data,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

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
