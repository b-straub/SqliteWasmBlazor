namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Local-only sync tracking metadata. Not synced.
///
/// <para>
/// A single deterministic row keyed by <see cref="EngineCursorId"/> holds
/// the per-device sync state: the export cursor
/// (<see cref="LastExportedAt"/>) used by <see cref="SyncEngine"/> on push,
/// and the receive cursor (<see cref="LastReceivedCursor"/>) used by
/// <see cref="EfReceiveCursorStore"/> on pull. Both survive process restart
/// because the row lives in the OPFS-hosted SQLite database — losing the
/// receive cursor on reload would replay every envelope ever delivered to
/// this recipient (P9 violation).
/// </para>
/// </summary>
public sealed class SyncState
{
    /// <summary>
    /// Deterministic id of the single per-DB sync-state row. Value chosen
    /// via <c>CryptoSyncContextBase.DeterministicGuid("SyncEngine.ExportCursor:v1")</c>;
    /// kept under that name for backwards compatibility now that the row
    /// also carries <see cref="LastReceivedCursor"/>.
    /// </summary>
    public static readonly Guid EngineCursorId =
        CryptoSyncContextBase.DeterministicGuid("SyncEngine.ExportCursor:v1");

    public Guid Id { get; set; }

    /// <summary>
    /// High-water mark timestamp through which the engine has exported.
    /// On first push the engine seeds a fresh row with this value;
    /// subsequent pushes update it.
    /// </summary>
    public DateTime LastExportedAt { get; set; }

    /// <summary>
    /// Highest relay cursor through which this device has pulled envelopes
    /// — the value passed as <c>?since=</c> on the next
    /// <c>GET /api/delta</c>. Must advance monotonically; resetting to 0
    /// causes the device to redownload every envelope ever delivered to its
    /// recipient pubkey (P9 violation, threat-model §7).
    /// </summary>
    public long LastReceivedCursor { get; set; }

    /// <summary>Reserved for future delta-hash tracking. Currently unused.</summary>
    public string? LastDeltaHash { get; set; }

    /// <summary>
    /// Highest whitelist version this admin device has successfully pushed to
    /// the relay. The next push is at <c>LastWhitelistVersion + 1</c>; on a
    /// 409 from the relay (concurrent admin push, multi-admin recovery) the
    /// caller updates this to the relay-reported <c>current_version</c> and
    /// retries. Non-admin devices never write this column — it stays 0.
    /// </summary>
    public long LastWhitelistVersion { get; set; }
}
