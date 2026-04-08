namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Thrown when a sync operation is rejected by a precondition guard.
/// The most common cause is the trusted-contact gate (sender unknown or
/// not at <see cref="TrustLevel.Full"/>); subclasses or messages may
/// indicate other reasons (signature failure, replay, etc.).
/// </summary>
public sealed class SyncRejectedException : InvalidOperationException
{
    public SyncRejectedException(string message) : base(message) { }
    public SyncRejectedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// First gate any incoming delta passes through. Verifies that the sender
/// is a known full-trust contact in this device's local Contacts table.
/// If the gate rejects, NO further work happens — no decryption, no shadow
/// writes, no open-table touches. The whole sync is blocked.
///
/// <para>
/// This is the "is the current client a trusted contact" precondition: a
/// device that doesn't appear in our contacts (or appears at less than Full
/// trust) cannot push any data into our state, regardless of valid
/// signatures or matching content keys.
/// </para>
///
/// <para>
/// The gate sits ABOVE everything else (envelope decryption, signature
/// verification, key unwrapping). Failing it short-circuits the import.
/// </para>
/// </summary>
public class SyncGate(ContactService contacts)
{
    /// <summary>
    /// Resolve and verify the sender of an incoming delta. Returns the
    /// <see cref="TrustedContact"/> on success; throws
    /// <see cref="SyncRejectedException"/> otherwise.
    /// </summary>
    /// <param name="senderEd25519PublicKey">
    /// The Ed25519 public key the delta was signed with (typically read
    /// from the envelope's plaintext sender field).
    /// </param>
    public async ValueTask<TrustedContact> EnsureSenderTrustedAsync(string senderEd25519PublicKey)
    {
        if (string.IsNullOrEmpty(senderEd25519PublicKey))
        {
            throw new SyncRejectedException("Sender public key is missing — envelope is malformed.");
        }

        var contact = await contacts.GetByEd25519PublicKeyAsync(senderEd25519PublicKey);
        if (contact is null)
        {
            // Don't echo the full key; show a fingerprint prefix for debugging.
            var hint = senderEd25519PublicKey.Length >= 16
                ? senderEd25519PublicKey[..16]
                : senderEd25519PublicKey;
            throw new SyncRejectedException(
                $"Sender is not a known contact on this device (key prefix: {hint}…). Sync blocked.");
        }

        if (contact.TrustLevel != TrustLevel.Full)
        {
            throw new SyncRejectedException(
                $"Sender '{contact.Username}' is at trust level {contact.TrustLevel}; sync requires {TrustLevel.Full}. " +
                "Promote the contact via ContactPromotionService.ElevateToFullAsync first.");
        }

        return contact;
    }
}
