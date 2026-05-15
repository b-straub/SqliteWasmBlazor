namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Signing capability scoped to this device's Ed25519 sync identity for the
/// <i>send</i> side of the whitelist-broadcast contract. Peer of
/// <see cref="IReceiveAuthSigner"/>; an actor that both sends and receives
/// holds both — typically backed by the same Ed25519 keypair, but the seam is
/// kept separate so production wiring (PRF / WebAuthn, Stage B) and test
/// stubs can swap them independently.
///
/// <para>
/// <see cref="HttpSyncTransport"/> uses this on every POST <c>/api/delta</c>
/// to attach the <c>X-Timestamp</c> / <c>X-Sender-PubKey</c> /
/// <c>X-Sender-Sig</c> headers. The relay verifies <c>sha256(salt || pubkey)</c>
/// against its whitelist (status <c>active</c>), then sodium-verifies the sig
/// over <c>"deltapost-v1|" + ts + "|" + sha256(envelope)</c>.
/// </para>
///
/// <para>
/// The implementer owns the priv-key access pattern (e.g. PRF-derived key
/// loaded on demand) and is responsible for zeroing the priv buffer after
/// each sign — same discipline as <c>ContactInvitationService</c>.
/// </para>
/// </summary>
public interface ISenderAuthSigner
{
    /// <summary>
    /// This device's Ed25519 public key (base64). Sent as the
    /// <c>X-Sender-PubKey</c> header on POST <c>/api/delta</c> and used by the
    /// relay as the verifier key for the request signature.
    /// </summary>
    string OwnEd25519PublicKeyBase64 { get; }

    /// <summary>
    /// Sign the send-challenge message
    /// <c>"deltapost-v1|{timestamp}|{sha256(envelope) hex}"</c> using the
    /// device's Ed25519 priv. Returns a base64-encoded 64-byte detached
    /// signature.
    /// </summary>
    ValueTask<string> SignSendChallengeAsync(string message, CancellationToken cancellationToken = default);
}
