using MessagePack;
using SqliteWasmBlazor.CryptoSync.Services;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Stub <see cref="ICryptoSyncDeltaService"/> that returns canned bytes for
/// the two methods <see cref="SyncOrchestrator"/> consumes. The rotate
/// surface throws — this fake exists exclusively to verify that the
/// orchestrator wires <see cref="IImportNotifier"/> through the import
/// path. It is <b>not</b> a crypto roundtrip; pipeline correctness is
/// covered by the browser-side <c>CryptoSyncRoundTripTest</c>.
///
/// <para>
/// Pre-three-plane-split this fake implemented <c>ISqliteWasmDatabaseService</c>
/// because the delta methods used to live there. After the split, the
/// orchestrator depends on <see cref="ICryptoSyncDeltaService"/> directly,
/// so the fake's surface narrowed to just the three delta methods.
/// </para>
/// </summary>
internal sealed class FakeDatabaseService : ICryptoSyncDeltaService
{
    public byte[] CannedExportBytes { get; init; } = [];
    public ImportReport CannedImportReport { get; init; } = new();

    public Task<byte[]> DeltaExportAsync(
        string databaseName,
        BulkExportMetadata exportMetadata,
        byte[] headerBytes,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CannedExportBytes);

    public Task<byte[]> DeltaImportAsync(
        string databaseName,
        byte[] headerBytes,
        byte[] envelopeBytes,
        CancellationToken cancellationToken = default)
        => Task.FromResult(MessagePackSerializer.Serialize(CannedImportReport));

    public Task<int> DeltaRotateKeyAsync(
        string databaseName, byte[] oldHeaderBytes, byte[] newHeaderBytes,
        string sharingId, int? newKeyVersion = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "FakeDatabaseService: rotate not exercised by orchestrator-import-notifier tests.");
}
