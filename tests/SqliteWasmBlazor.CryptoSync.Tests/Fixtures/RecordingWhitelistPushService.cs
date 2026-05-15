namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Test-only <see cref="IWhitelistPushService"/> that records every push and
/// returns success without touching HTTP. Used by in-memory invitation /
/// promotion / revocation scenarios that don't care about wire-level
/// whitelist enforcement (the live-relay tests cover that path against
/// Herd-served PHP).
///
/// <para>
/// Each entry in <see cref="Pushes"/> is the exact arguments handed to
/// <see cref="PushAsync"/>; the returned <see cref="WhitelistPushResult"/>
/// carries the same <c>version</c> the caller supplied, so version-tracking
/// logic in consumers (e.g. <c>SyncState.LastWhitelistVersion</c>) advances
/// the same way it would against a real relay.
/// </para>
/// </summary>
public sealed class RecordingWhitelistPushService : IWhitelistPushService
{
    public List<Recorded> Pushes { get; } = [];

    public ValueTask<WhitelistPushResult> PushAsync(
        IReadOnlyList<WhitelistOp> operations,
        string adminEd25519PublicKeyBase64,
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        long version,
        CancellationToken cancellationToken = default)
    {
        Pushes.Add(new Recorded(version, [.. operations], adminEd25519PublicKeyBase64));
        return ValueTask.FromResult(new WhitelistPushResult(version, operations.Count));
    }

    public sealed record Recorded(
        long Version,
        IReadOnlyList<WhitelistOp> Operations,
        string AdminEd25519PublicKeyBase64);
}
