using MessagePack;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Stub <see cref="ISqliteWasmDatabaseService"/> that returns canned bytes
/// for the two delta methods <see cref="SyncOrchestrator"/> consumes. The
/// rest of the surface throws — this fake is exclusively for verifying that
/// the orchestrator wires <see cref="IImportNotifier"/> through the import
/// path. It is <b>not</b> a crypto roundtrip; pipeline correctness is
/// covered by the browser-side <c>CryptoSyncRoundTripTest</c>.
/// </summary>
internal sealed class FakeDatabaseService : ISqliteWasmDatabaseService
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

    // --- unused surface ---

    public Task<bool> ExistsDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task RenameDatabaseAsync(string oldName, string newName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task CloseDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task OpenDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<VfsImportResult> ImportDatabaseAsync(string databaseName, byte[] data, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<byte[]> ExportDatabaseAsync(string databaseName, VfsExportMode mode = VfsExportMode.VERBATIM, ReadOnlyMemory<byte> newKey = default, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<int> ImportRowsAsync(string databaseName, byte[] data, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<int> DeltaRotateKeyAsync(string databaseName, byte[] oldHeaderBytes, byte[] newHeaderBytes, string sharingId, int? newKeyVersion = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<VfsKeyInstallResult> InstallEncryptionKeyAsync(string databaseName, ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task ClearEncryptionKeyAsync(string databaseName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task EncryptDatabaseInPlaceAsync(string databaseName, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task DecryptDatabaseInPlaceAsync(string databaseName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
