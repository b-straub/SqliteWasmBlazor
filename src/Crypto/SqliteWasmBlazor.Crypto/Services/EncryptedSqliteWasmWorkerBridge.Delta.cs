// SqliteWasmBlazor.Crypto — encrypted delta export / import / rotate-key
// MIT License

using System.Security.Cryptography;

namespace SqliteWasmBlazor.Crypto.Services;

// Delta partial: CryptoSync's encrypted delta export, import (with staggered
// system-then-domain apply), and in-place per-sharingId key rotation.
internal sealed partial class EncryptedSqliteWasmWorkerBridge
{
    internal async Task<byte[]> DeltaExportAsync(
        string databaseName, BulkExportMetadata exportMetadata,
        byte[] headerBytes, CancellationToken cancellationToken = default)
    {
        // Binary payload = MessagePack-serialized CryptoHeader (opaque to the
        // base bridge layer — only the worker parses it). Metadata JSON carries
        // the BulkExportMetadata so the worker can reuse the existing export
        // path to read rows from the open table.
        var dataDict = System.Text.Json.JsonSerializer.SerializeToNode(
            exportMetadata, SqliteWasmWorkerBridge.JsonOptions)?.AsObject()
            ?? new System.Text.Json.Nodes.JsonObject();
        dataDict["type"] = "deltaExportEncrypted";
        dataDict["database"] = databaseName;

        try
        {
            return await _bridge.PostBinaryForBytesAsync(
                dataDict, headerBytes, cancellationToken, TimeSpan.FromMinutes(5));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Encrypted delta export timed out.");
        }
    }

    internal async Task<byte[]> DeltaImportAsync(
        string databaseName, byte[] headerBytes,
        byte[] envelopeBytes, CancellationToken cancellationToken = default)
    {
        // Binary payload = CryptoHeader, binary header = DeltaEnvelope.
        // Worker dispatches as 'deltaImportEncrypted' which consumes a
        // multi-group envelope and staggers system tables first.
        try
        {
            return await _bridge.PostBinaryWithHeaderForBytesAsync(
                new
                {
                    type = "deltaImportEncrypted",
                    database = databaseName
                },
                headerBytes, envelopeBytes, cancellationToken,
                TimeSpan.FromMinutes(5));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Encrypted delta import timed out.");
        }
        finally
        {
            // headerBytes carries CryptoHeader private-key material; the JS
            // bridge sliced it into a transferred ArrayBuffer, but the C# copy
            // is still in the managed heap — zero it before GC.
            CryptographicOperations.ZeroMemory(headerBytes);
        }
    }

    internal async Task<int> DeltaRotateKeyAsync(
        string databaseName,
        byte[] oldHeaderBytes, byte[] newHeaderBytes,
        string sharingId, int? newKeyVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sharingId))
        {
            throw new ArgumentException(
                "sharingId is required — rotate now walks every shadow table for matching rows",
                nameof(sharingId));
        }

        try
        {
            // binaryPayload = old CryptoHeader, binaryHeader = new CryptoHeader.
            // The worker walks every _crypto_* shadow table, rotating rows
            // whose SharingId matches — so a parent-child group whose rows
            // span tables (List + Items) rotates atomically.
            var result = await _bridge.PostBinaryWithHeaderAsync(
                new
                {
                    type = "bulkRotateKey",
                    database = databaseName,
                    sharingId,
                    newKeyVersion
                },
                oldHeaderBytes, newHeaderBytes, cancellationToken,
                TimeSpan.FromMinutes(5));
            return result.RowsAffected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Bulk key rotation timed out.");
        }
        finally
        {
            // Both buffers contain CryptoHeader private-key material. The JS
            // bridge transfers slice copies to the worker; the C# originals
            // remain in the managed heap until zeroed here.
            CryptographicOperations.ZeroMemory(oldHeaderBytes);
            CryptographicOperations.ZeroMemory(newHeaderBytes);
        }
    }
}
