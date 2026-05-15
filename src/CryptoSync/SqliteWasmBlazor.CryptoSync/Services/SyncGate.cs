using SqliteWasmBlazor.CryptoSync.Abstractions;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Thrown when a sync operation is rejected by a precondition guard.
/// </summary>
public sealed class SyncRejectedException : InvalidOperationException
{
    public SyncRejectedException(string message) : base(message) { }
    public SyncRejectedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// First gate any incoming delta passes through. Verifies that the sender
/// is a known trusted contact. If the gate rejects, no further work happens.
/// </summary>
internal class SyncGate(IContactService contacts) : ISyncGate
{
    /// <summary>
    /// Resolve and verify the sender. Returns the <see cref="TrustedContact"/>
    /// on success; throws <see cref="SyncRejectedException"/> otherwise.
    /// </summary>
    public async ValueTask<TrustedContact> EnsureSenderTrustedAsync(string senderEd25519PublicKey)
    {
        if (string.IsNullOrEmpty(senderEd25519PublicKey))
        {
            throw new SyncRejectedException("Sender public key is missing — envelope is malformed.");
        }

        var contact = await contacts.GetByEd25519PublicKeyAsync(senderEd25519PublicKey);
        if (contact is null)
        {
            var hint = senderEd25519PublicKey.Length >= 16
                ? senderEd25519PublicKey[..16]
                : senderEd25519PublicKey;
            throw new SyncRejectedException(
                $"Sender is not a known contact on this device (key prefix: {hint}…). Sync blocked.");
        }

        return contact;
    }
}
