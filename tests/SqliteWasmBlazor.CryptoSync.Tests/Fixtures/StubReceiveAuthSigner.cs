namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Deterministic <see cref="IReceiveAuthSigner"/> for <see cref="HttpSyncTransport"/>
/// unit tests. Records every signed message and returns a fixed signature so
/// assertions can verify the wire format without running real Ed25519 — the
/// crypto round-trip belongs in the integration test against the live PHP
/// relay, not these transport-shape unit tests.
/// </summary>
internal sealed class StubReceiveAuthSigner : IReceiveAuthSigner
{
    public required string OwnEd25519PublicKeyBase64 { get; init; }
    public string SignatureToReturn { get; init; } = "fake-sig";
    public List<string> SignedMessages { get; } = [];

    public ValueTask<string> SignReceiveChallengeAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        SignedMessages.Add(message);
        return ValueTask.FromResult(SignatureToReturn);
    }
}
