// SqliteWasmBlazor.CryptoSync — encrypted delta-bulk row I/O
// MIT License

namespace SqliteWasmBlazor.CryptoSync.Services;

/// <summary>
/// CryptoSync's encrypted shadow-row sync surface. Lives in the
/// CryptoSync package — base SqliteWasm consumers never see this
/// interface. Encrypted deltas are inherently a CryptoSync concept
/// (multi-device sync of permission-gated rows over an encrypted VFS),
/// not a general SQLite-on-OPFS feature.
///
/// <para>
/// <b>Audience.</b> CryptoSync apps wiring multi-device sync. Plain
/// SQLite-on-OPFS apps and encrypted-but-non-sync apps don't reference
/// this interface and never need to register it.
/// </para>
///
/// <para>
/// <b>Implementation.</b> Thin C# wrapper over the now-internal worker
/// bridge methods (<c>SqliteWasmWorkerBridge.DeltaExportAsync</c> /
/// <c>DeltaImportAsync</c> / <c>DeltaRotateKeyAsync</c>). The bridge owns
/// the worker round-trip, key unwrap (CEK via ECDH+HKDF), per-row AEAD,
/// per-row Ed25519 signing, shadow-table upserts, and permission
/// enforcement. This interface just gives consumers a clean DI seam.
/// </para>
/// </summary>
public interface ICryptoSyncDeltaService
{
    /// <summary>
    /// Encrypted bulk export — shadow rows as wire format. Worker
    /// derives CEK via crypto-core (ECDH + HKDF), encrypts per-row with
    /// AAD (Layer 1 tamper detection), signs per-row (Layer 2), upserts
    /// shadow, returns MessagePack-packed ShadowRowGroup.
    /// </summary>
    Task<byte[]> DeltaExportAsync(string databaseName,
        BulkExportMetadata exportMetadata, byte[] headerBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypted bulk import with three-layer tamper detection.
    /// Consumes a MessagePack-packed <c>DeltaEnvelope</c> (multi-group,
    /// multi-table). Worker verifies outer Ed25519 envelope signature,
    /// staggers groups so system tables (Contacts/ShareGroups/ShareTargets)
    /// land first, then for each group: verifies the batch signature
    /// (Layer 2), unwraps CEK (Layer 3), decrypts with AAD (Layer 1),
    /// enforces permissions, applies to shadow + open tables. Returns
    /// MessagePack-packed aggregated ImportReport.
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="headerBytes">MessagePack-serialized CryptoHeader.</param>
    /// <param name="envelopeBytes">MessagePack-packed DeltaEnvelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MessagePack-packed ImportReport bytes.</returns>
    Task<byte[]> DeltaImportAsync(string databaseName, byte[] headerBytes,
        byte[] envelopeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-encrypt every shadow row sharing the given <paramref name="sharingId"/>
    /// under a new content key, in place, inside a single SQLite
    /// transaction. The worker walks every <c>_crypto_*</c> shadow table
    /// and re-encrypts matching rows across all of them — so a sharing
    /// group whose descendants span multiple tables (e.g. List + Items)
    /// rotates atomically. Unwraps old + new CEKs from two CryptoHeaders
    /// inside the worker — raw key material never leaves the worker.
    /// </summary>
    /// <param name="databaseName">Target database filename.</param>
    /// <param name="oldHeaderBytes">MessagePack-serialized CryptoHeader for the old key version.</param>
    /// <param name="newHeaderBytes">MessagePack-serialized CryptoHeader for the new key version.</param>
    /// <param name="sharingId">
    /// SharingId of the rows to rotate — every shadow row matching this
    /// value across every table gets re-encrypted with the new CEK.
    /// </param>
    /// <param name="newKeyVersion">Optional new key version to stamp on rotated rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of shadow rows re-encrypted (across all tables).</returns>
    Task<int> DeltaRotateKeyAsync(string databaseName,
        byte[] oldHeaderBytes, byte[] newHeaderBytes,
        string sharingId, int? newKeyVersion = null,
        CancellationToken cancellationToken = default);
}
