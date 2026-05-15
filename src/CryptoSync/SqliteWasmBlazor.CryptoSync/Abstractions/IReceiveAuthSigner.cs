namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Signing capability scoped to this device's Ed25519 sync identity. Used by
/// <see cref="HttpSyncTransport"/> to sign the receive-side challenge that
/// proves to the relay "I own the inbox I'm trying to drain". Per the relay
/// design doc, no inbox-draining auth is needed for confidentiality (the V2
/// envelope already provides that), but it stops a passive observer who
/// learns the pubkey from learning metadata about who's writing to whom.
///
/// <para>
/// The implementer owns the priv-key access pattern (e.g. PRF-derived key
/// loaded on demand) and is responsible for zeroing the priv buffer after
/// each sign — same discipline as <c>ContactInvitationService</c>.
/// </para>
/// </summary>
public interface IReceiveAuthSigner
{
    /// <summary>
    /// This device's Ed25519 public key (base64). Sent as the
    /// <c>recipient</c> query parameter on receive GETs and used by the
    /// relay as the verifier key for the request signature.
    /// </summary>
    string OwnEd25519PublicKeyBase64 { get; }

    /// <summary>
    /// Sign the receive-challenge message <c>"{timestamp}|{ownPubKey}"</c>
    /// using the device's Ed25519 priv. Returns a base64-encoded 64-byte
    /// detached signature.
    /// </summary>
    ValueTask<string> SignReceiveChallengeAsync(string message, CancellationToken cancellationToken = default);
}
