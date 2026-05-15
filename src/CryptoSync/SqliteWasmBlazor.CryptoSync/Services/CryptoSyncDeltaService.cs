// SqliteWasmBlazor.CryptoSync — encrypted delta-bulk row I/O
// MIT License

using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync.Services;

/// <summary>
/// Production implementation of <see cref="ICryptoSyncDeltaService"/>.
/// Thin wrapper that resolves <see cref="EncryptedSqliteWasmWorkerBridge.Instance"/>
/// (plane-2's bridge singleton) and delegates each call to its now-internal
/// delta methods. The bridge methods are <c>internal</c> and made visible to
/// this assembly via <c>InternalsVisibleTo("SqliteWasmBlazor.CryptoSync")</c>
/// in <c>SqliteWasmBlazor.Crypto</c>.
/// </summary>
internal sealed class CryptoSyncDeltaService : ICryptoSyncDeltaService
{
    private readonly EncryptedSqliteWasmWorkerBridge _bridge = EncryptedSqliteWasmWorkerBridge.Instance;

    public Task<byte[]> DeltaExportAsync(string databaseName,
        BulkExportMetadata exportMetadata, byte[] headerBytes,
        CancellationToken cancellationToken = default)
        => _bridge.DeltaExportAsync(databaseName, exportMetadata, headerBytes, cancellationToken);

    public Task<byte[]> DeltaImportAsync(string databaseName, byte[] headerBytes,
        byte[] envelopeBytes, CancellationToken cancellationToken = default)
        => _bridge.DeltaImportAsync(databaseName, headerBytes, envelopeBytes, cancellationToken);

    public Task<int> DeltaRotateKeyAsync(string databaseName,
        byte[] oldHeaderBytes, byte[] newHeaderBytes,
        string sharingId, int? newKeyVersion = null,
        CancellationToken cancellationToken = default)
        => _bridge.DeltaRotateKeyAsync(databaseName, oldHeaderBytes, newHeaderBytes,
            sharingId, newKeyVersion, cancellationToken);
}
