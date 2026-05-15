namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Byte-opaque, broadcast delivery for the sync layer. Carries whatever the
/// orchestrator (or a peer-bootstrap flow like contact invitations) hands it —
/// typically a MessagePack-serialized <see cref="DeltaEnvelope"/>, but the
/// contract is just bytes.
///
/// <para>
/// <b>Relay semantics.</b> Implementations target a "dumb broadcast buffer":
/// the relay accepts envelopes from any whitelisted (status <c>active</c>)
/// sender, stores one row per envelope, and serves a single global stream to
/// any whitelisted puller. The relay never inspects payloads — every
/// confidentiality / integrity guarantee comes from the layer above (V2
/// envelope crypto for deltas, Ed25519 signatures for invitations). The
/// receiver's crypto layer drops envelopes addressed to keys it doesn't hold.
/// </para>
///
/// <para>
/// <b>Identity.</b> The send side is authenticated at the network layer via
/// <see cref="ISenderAuthSigner"/> (Ed25519 sig over
/// <c>"deltapost-v1|{ts}|{sha256(envelope)}"</c>); the receive side via
/// <see cref="IReceiveAuthSigner"/>. Both signers are injected into the
/// transport by DI; <see cref="ISyncTransport"/> itself stays addressee-free.
/// </para>
/// </summary>
public interface ISyncTransport
{
    /// <summary>
    /// Hand <paramref name="envelope"/> to the relay for broadcast delivery.
    /// Returns once the relay has accepted the bytes — <b>not</b> once
    /// receivers have downloaded them. The implementation authenticates the
    /// POST via the configured <see cref="ISenderAuthSigner"/>; the relay
    /// rejects senders whose pubkey hash is not <c>active</c> on the
    /// whitelist.
    /// </summary>
    ValueTask SendAsync(
        byte[] envelope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Try to pull the next pending envelope from the broadcast stream.
    /// Returns <c>null</c> when the stream has nothing past the receiver's
    /// cursor — callers poll or await a higher-level signal (e.g. push
    /// notification) to learn when to retry.
    /// </summary>
    ValueTask<byte[]?> TryReceiveAsync(CancellationToken cancellationToken = default);
}
