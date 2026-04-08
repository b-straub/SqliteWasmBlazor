using BlazorPRF.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Wire format for an ECIES-wrapped content key (or any
/// <see cref="EncryptedMessage"/>) — the byte layout used both in
/// <c>SharingKey.WrappedContentKey</c> at rest and inside delta envelopes
/// over the wire.
///
/// <para>
/// Format: <c>[ephPkLen(1) | ephPk | nonceLen(1) | nonce | ciphertext]</c>
/// </para>
///
/// <para>
/// Single-byte length prefixes are sufficient because all the lengths involved
/// (X25519 public key = 32 bytes, AES-GCM nonce = 12 bytes) are well under 256.
/// The ciphertext takes the remainder of the buffer.
/// </para>
///
/// <para>
/// Both <see cref="CryptoSyncBootstrap"/> (which writes the admin's
/// self-SharingKey) and <see cref="SyncOrchestrator"/> (which reads recipient
/// envelopes during import) need this format. Hoisted out of SyncOrchestrator
/// so there's a single source of truth.
/// </para>
/// </summary>
public static class EnvelopeBytes
{
    /// <summary>
    /// Pack an <see cref="EncryptedMessage"/> (the result of
    /// <c>ICryptoProvider.EncryptAsymmetricAsync</c>) into the wire byte format.
    /// </summary>
    public static byte[] Serialize(EncryptedMessage msg)
    {
        var ephPk = Convert.FromBase64String(msg.EphemeralPublicKey);
        var ct = Convert.FromBase64String(msg.Ciphertext);
        var nonce = Convert.FromBase64String(msg.Nonce);

        var result = new byte[1 + ephPk.Length + 1 + nonce.Length + ct.Length];
        result[0] = (byte)ephPk.Length;
        ephPk.CopyTo(result.AsSpan(1));
        result[1 + ephPk.Length] = (byte)nonce.Length;
        nonce.CopyTo(result.AsSpan(2 + ephPk.Length));
        ct.CopyTo(result.AsSpan(2 + ephPk.Length + nonce.Length));
        return result;
    }

    /// <summary>
    /// Unpack a wire-format buffer back into an <see cref="EncryptedMessage"/>
    /// suitable for <c>ICryptoProvider.DecryptAsymmetricAsync</c>.
    /// </summary>
    public static EncryptedMessage Deserialize(byte[] data)
    {
        var ephPkLen = data[0];
        var ephPk = data.AsSpan(1, ephPkLen);
        var nonceLen = data[1 + ephPkLen];
        var nonce = data.AsSpan(2 + ephPkLen, nonceLen);
        var ct = data.AsSpan(2 + ephPkLen + nonceLen);

        return new EncryptedMessage(
            Convert.ToBase64String(ephPk),
            Convert.ToBase64String(ct),
            Convert.ToBase64String(nonce)
        );
    }
}
