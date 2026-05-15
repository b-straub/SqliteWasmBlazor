namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// First gate any incoming delta passes through. Verifies that the sender's
/// Ed25519 public key resolves to a known <see cref="TrustedContact"/> on
/// this device. If the gate rejects, no further work happens — the import
/// pipeline short-circuits before any decrypt / apply.
/// </summary>
public interface ISyncGate
{
    /// <summary>
    /// Resolve and verify the sender. Returns the <see cref="TrustedContact"/>
    /// on success; throws <see cref="SyncRejectedException"/> otherwise.
    /// </summary>
    ValueTask<TrustedContact> EnsureSenderTrustedAsync(string senderEd25519PublicKey);
}
