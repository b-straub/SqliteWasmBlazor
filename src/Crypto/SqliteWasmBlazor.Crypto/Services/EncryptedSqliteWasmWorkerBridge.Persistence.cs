// SqliteWasmBlazor.Crypto — encrypted import-rekey / verify / mode-export
// MIT License

using System.Security.Cryptography;
using MessagePack;

namespace SqliteWasmBlazor.Crypto.Services;

// Persistence partial: the import-rekey + verify-rekey + mode-aware export
// paths. All three consume a 32-byte key (either the wrap-key for an
// envelope import or a new key for export-rekey).
internal sealed partial class EncryptedSqliteWasmWorkerBridge
{
    /// <summary>
    /// Asymmetric import with auto-rekey: the envelope ships its page bytes
    /// already encrypted under a per-export <c>wrapKey</c>; the worker's
    /// <c>rekeySlots</c> decrypts each slot under <paramref name="wrapKey"/>
    /// then re-encrypts under the currently-installed global key. Returns
    /// <see cref="DiskImportResult.OK"/> / <see cref="DiskImportResult.WRONG_KEY"/>
    /// / <see cref="DiskImportResult.EXISTING_DB_REFUSED"/>.
    /// </summary>
    internal async Task<DiskImportResult> ImportDatabaseWithRekeyAsync(
        string databaseName,
        byte[] wrapKey,
        byte[] dbBytes,
        CancellationToken cancellationToken = default)
    {
        if (wrapKey.Length != 32)
        {
            throw new ArgumentException(
                $"wrapKey must be exactly 32 bytes, got {wrapKey.Length}.",
                nameof(wrapKey));
        }

        var envelope = new VfsImportRekeyEnvelope
        {
            Version = 1,
            WrapKey = wrapKey,
            DbBytes = dbBytes,
            AadVersion = "v1",
        };
        byte[] envelopeBytes;
        try
        {
            envelopeBytes = MessagePackSerializer.Serialize(envelope);
        }
        finally
        {
            envelope.Clear();
        }

        try
        {
            SqlQueryResult result;
            try
            {
                result = await _bridge.PostBinaryAsync(
                    new { type = "importDbRekey", database = databaseName },
                    envelopeBytes,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Import-rekey database operation timed out.");
            }

            _bridge.MarkDatabaseClosed(databaseName);

            return result.RowsAffected switch
            {
                0 => DiskImportResult.OK,
                1 => DiskImportResult.WRONG_KEY,
                2 => DiskImportResult.EXISTING_DB_REFUSED,
                var other => throw new InvalidOperationException(
                    $"Worker returned unexpected import-rekey outcome code {other}"),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    /// <summary>
    /// Plain-source import onto an Encrypted+Unlocked disk. Ships raw
    /// plain SQLite bytes to the worker's <c>importDbPlain</c> handler,
    /// which re-encrypts every page under the registered globalKey via
    /// <c>rekeySlots</c> before routing through the opaque-import path.
    /// Mirror of <see cref="ImportDatabaseWithRekeyAsync"/> minus the
    /// transit wrap key — there's no per-export key to ship because the
    /// source bytes are plaintext.
    /// </summary>
    internal async Task<DiskImportResult> ImportPlainDatabaseAsync(
        string databaseName,
        byte[] plainBytes,
        CancellationToken cancellationToken = default)
    {
        SqlQueryResult result;
        try
        {
            result = await _bridge.PostBinaryAsync(
                new { type = "importDbPlain", database = databaseName },
                plainBytes,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Import-plain database operation timed out.");
        }

        _bridge.MarkDatabaseClosed(databaseName);

        return result.RowsAffected switch
        {
            0 => DiskImportResult.OK,
            1 => DiskImportResult.WRONG_KEY,
            2 => DiskImportResult.EXISTING_DB_REFUSED,
            var other => throw new InvalidOperationException(
                $"Worker returned unexpected import-plain outcome code {other}"),
        };
    }

    /// <summary>
    /// Non-destructive AEAD preflight for asymmetric import. Runs the
    /// worker's <c>rekeySlots</c> decrypt half against the envelope's page
    /// ciphertext under the unwrapped <paramref name="wrapKey"/>, discards
    /// the plaintext, returns OK / WRONG_KEY. No OPFS write — caller uses
    /// this to validate every file in the envelope BEFORE wiping the
    /// existing pool, preserving the disk-as-unit invariant: a corrupt
    /// envelope must not destroy the user's data even part-way through a
    /// multi-file import.
    /// </summary>
    internal async Task<DiskImportResult> VerifyImportRekeyAsync(
        string databaseName,
        byte[] wrapKey,
        byte[] dbBytes,
        CancellationToken cancellationToken = default)
    {
        if (wrapKey.Length != 32)
        {
            throw new ArgumentException(
                $"wrapKey must be exactly 32 bytes, got {wrapKey.Length}.",
                nameof(wrapKey));
        }

        var envelope = new VfsImportRekeyEnvelope
        {
            Version = 1,
            WrapKey = wrapKey,
            DbBytes = dbBytes,
            AadVersion = "v1",
        };
        var envelopeBytes = MessagePackSerializer.Serialize(envelope);

        try
        {
            SqlQueryResult result;
            try
            {
                result = await _bridge.PostBinaryAsync(
                    new { type = "verifyImportRekey", database = databaseName },
                    envelopeBytes,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("VerifyImportRekey operation timed out.");
            }

            return result.RowsAffected switch
            {
                0 => DiskImportResult.OK,
                1 => DiskImportResult.WRONG_KEY,
                var other => throw new InvalidOperationException(
                    $"Worker returned unexpected verify-import-rekey outcome code {other}"),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
            envelope.Clear();
        }
    }

    /// <summary>
    /// Mode-aware export. <see cref="VfsExportMode.VERBATIM"/> /
    /// <see cref="VfsExportMode.PLAIN"/> ships through plane-1 directly
    /// (no key envelope); <see cref="VfsExportMode.REKEY"/> /
    /// <see cref="VfsExportMode.ENCRYPT"/> rekey each page under
    /// <paramref name="newKey"/>.
    /// </summary>
    internal Task<byte[]> ExportDatabaseAsync(
        string databaseName,
        VfsExportMode mode,
        ReadOnlyMemory<byte> newKey,
        CancellationToken cancellationToken = default)
    {
        if (mode == VfsExportMode.REKEY || mode == VfsExportMode.ENCRYPT)
        {
            if (newKey.Length != 32)
            {
                throw new ArgumentException(
                    $"newKey must be exactly 32 bytes for mode={mode}, got {newKey.Length}",
                    nameof(newKey));
            }
            return ExportWithKeyAsync(databaseName, mode, newKey, cancellationToken);
        }

        if (!newKey.IsEmpty)
        {
            throw new ArgumentException(
                $"newKey must be empty for mode={mode}; only mode={VfsExportMode.REKEY} and mode={VfsExportMode.ENCRYPT} accept a key",
                nameof(newKey));
        }

        // VERBATIM / PLAIN — no key envelope, delegate to plane-1.
        var modeWire = mode == VfsExportMode.PLAIN ? "plain" : "verbatim";
        return _bridge.SendRawBinaryRequestAsync(
            databaseName,
            new { type = "exportDb", database = databaseName, mode = modeWire },
            $"Export {modeWire}",
            cancellationToken);
    }

    private async Task<byte[]> ExportWithKeyAsync(
        string databaseName,
        VfsExportMode mode,
        ReadOnlyMemory<byte> newKey,
        CancellationToken cancellationToken)
    {
        var header = new VfsKeyHeader
        {
            Version = 1,
            Key = newKey.ToArray(),
            AadVersion = "v1",
        };
        var envelope = MessagePackSerializer.Serialize(header);
        var modeWire = mode == VfsExportMode.REKEY ? "rekey" : "encrypt";

        try
        {
            byte[] result;
            try
            {
                result = await _bridge.PostBinaryForBytesAsync(
                    new { type = "exportDb", database = databaseName, mode = modeWire },
                    envelope,
                    cancellationToken,
                    TimeSpan.FromMinutes(1));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Export rekey timed out after 60 seconds.");
            }

            // Worker closes the DB during export for a consistent snapshot.
            _bridge.MarkDatabaseClosed(databaseName);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            header.Clear();
        }
    }
}
