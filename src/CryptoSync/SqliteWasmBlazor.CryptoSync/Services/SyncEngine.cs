using SqliteWasmBlazor.Crypto.Abstractions.Models;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Glue between <see cref="SyncOrchestrator"/> (export/import via the worker)
/// and <see cref="ISyncTransport"/> (whitelist-broadcast relay delivery).
/// One instance per <see cref="CryptoSyncContextBase"/> + relay pair. Holds
/// the per-DB cursor (last exported timestamp) so subsequent pushes ship only
/// rows the sender hasn't shipped before.
///
/// <para>
/// <b>Broadcast model.</b> Push hands the envelope to the transport with no
/// addressee — the relay accepts from any whitelisted (status <c>active</c>)
/// sender and serves a single global stream. The receiver's crypto layer
/// filters: rows whose scope CEK the receiver lacks decrypt-fail and are
/// dropped silently.
/// </para>
///
/// <para>
/// <b>Identity seam:</b> caller passes <see cref="DualKeyPairFull"/> per call
/// for now. Stage B replaces this with a PRF-derived signing capability so
/// raw priv bytes don't live on the SyncEngine surface.
/// </para>
/// </summary>
internal class SyncEngine(
    CryptoSyncContextBase context,
    ICryptoSyncDeltaService deltaService,
    ISyncTransport transport,
    IImportNotifier importNotifier,
    string databaseName)
{
    private DateTime? _lastExportedAtCache;

    /// <summary>
    /// High-water mark timestamp through which this engine has exported.
    /// Backed by the deterministic <see cref="SyncState"/> row keyed by
    /// <see cref="SyncState.EngineCursorId"/> — survives process restarts.
    /// Returns <see cref="DateTime.MinValue"/> until the first push (or first
    /// load). Cached after first read to avoid hitting the DB on every push.
    /// </summary>
    public async ValueTask<DateTime> GetLastExportedAtAsync(CancellationToken cancellationToken = default)
    {
        if (_lastExportedAtCache is { } cached)
        {
            return cached;
        }
        var row = await context.SyncStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SyncState.EngineCursorId, cancellationToken)
            .ConfigureAwait(false);
        var value = row?.LastExportedAt ?? default;
        _lastExportedAtCache = value;
        return value;
    }

    /// <summary>
    /// Push then pull. Returns the number of rows applied locally from
    /// the inbox drain.
    /// </summary>
    public async ValueTask<int> SyncOnceAsync(
        DualKeyPairFull ownKeys,
        CancellationToken cancellationToken = default)
    {
        await PushChangesAsync(ownKeys, cancellationToken).ConfigureAwait(false);
        return await PullChangesAsync(ownKeys, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Export local changes since <see cref="LastExportedAt"/> and broadcast
    /// the envelope via the transport. No-op if there are no changes; the
    /// cursor advances either way.
    /// </summary>
    public async ValueTask<bool> PushChangesAsync(
        DualKeyPairFull ownKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownKeys);

        var orchestrator = new SyncOrchestrator(deltaService, context, importNotifier);
        var lastExported = await GetLastExportedAtAsync(cancellationToken).ConfigureAwait(false);
        var since = lastExported == default ? (DateTime?)null : lastExported;
        var now = DateTime.UtcNow;

        var header = await BuildHeaderAsync(ownKeys, cancellationToken).ConfigureAwait(false);
        // ExportAsync calls header.Clear() in its finally, so we don't need to.
        var envelopeBytes = await orchestrator.ExportAsync(
            databaseName, header, since, cancellationToken).ConfigureAwait(false);

        if (envelopeBytes.Length == 0)
        {
            return false;
        }

        // Skip empty-body envelopes — nothing changed since the last cursor.
        var parsed = MessagePackSerializer.Deserialize<DeltaEnvelope>(envelopeBytes);
        var rowCount = 0;
        foreach (var group in parsed.Groups)
        {
            rowCount += group.Rows.Count;
        }
        if (rowCount == 0)
        {
            await SaveCursorAsync(now, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transport.SendAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
        await SaveCursorAsync(now, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask SaveCursorAsync(DateTime cursor, CancellationToken cancellationToken)
    {
        var row = await context.SyncStates
            .FirstOrDefaultAsync(s => s.Id == SyncState.EngineCursorId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            context.SyncStates.Add(new SyncState
            {
                Id = SyncState.EngineCursorId,
                LastExportedAt = cursor
            });
        }
        else
        {
            row.LastExportedAt = cursor;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _lastExportedAtCache = cursor;
    }

    /// <summary>
    /// Drain the inbox and apply each envelope via
    /// <see cref="SyncOrchestrator.ImportAsync"/>. Envelopes from unknown
    /// senders surface as <see cref="SyncRejectedException"/> in the
    /// underlying import pipeline — currently propagated to the caller; a
    /// future variant could swallow + log per-envelope.
    /// </summary>
    /// <returns>Total rows applied across all drained envelopes.</returns>
    public async ValueTask<int> PullChangesAsync(
        DualKeyPairFull ownKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownKeys);

        var orchestrator = new SyncOrchestrator(deltaService, context, importNotifier);
        var totalApplied = 0;

        while (true)
        {
            var envelopeBytes = await transport.TryReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (envelopeBytes is null)
            {
                break;
            }

            // ImportAsync calls header.Clear() in its finally; rebuild per call.
            var header = await BuildHeaderAsync(ownKeys, cancellationToken).ConfigureAwait(false);
            var report = await orchestrator.ImportAsync(
                databaseName, header, envelopeBytes, cancellationToken).ConfigureAwait(false);
            totalApplied += report.RowsImported;
        }

        return totalApplied;
    }

    /// <summary>
    /// Build the CryptoHeader for an export or import call against the local
    /// system group. Materializes own contact id from
    /// <see cref="DeviceSettings.OwnContactId"/>, system group context +
    /// version + own wrapped CEK from the local <see cref="ShareGroup"/> /
    /// <see cref="ShareTarget"/> rows.
    /// </summary>
    private async Task<CryptoHeader> BuildHeaderAsync(
        DualKeyPairFull ownKeys, CancellationToken cancellationToken)
    {
        var ownContactId = await context.DeviceSettings
            .AsNoTracking()
            .Select(d => d.OwnContactId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "SyncEngine: DeviceSettings.OwnContactId is null. Bootstrap must set this before sync runs.");

        var systemGroup = await context.ShareGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "SyncEngine: system ShareGroup not found in local DB.");

        var ownTarget = await context.ShareTargets
            .AsNoTracking()
            .FirstOrDefaultAsync(t =>
                t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == ownKeys.X25519PublicKey
                && t.KeyVersion == systemGroup.KeyVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "SyncEngine: this device's own system ShareTarget not found. " +
                "Either bootstrap is incomplete or ownKeys does not match the local actor.");

        return new CryptoHeader
        {
            Version = 2,
            SystemTables = ["Contacts", "ShareGroups", "ShareTargets", "Invitations"],
            ClientContactId = ownContactId,
            ClientX25519PrivateKey = ownKeys.X25519PrivateKey,
            AdminX25519PublicKey = Convert.FromBase64String(systemGroup.GroupAdminPublicKey),
            GroupContext = systemGroup.GroupContext,
            KeyVersion = systemGroup.KeyVersion,
            WrappedCek = ownTarget.WrappedContentKey,
            ClientEd25519PrivateKey = ownKeys.Ed25519PrivateKey,
            ClientEd25519PublicKey = Convert.FromBase64String(ownKeys.Ed25519PublicKey)
        };
    }

}
