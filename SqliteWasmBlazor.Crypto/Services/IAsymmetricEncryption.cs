using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Service for asymmetric (ECIES) encryption using X25519 + AES-256-GCM.
/// </summary>
public interface IAsymmetricEncryption
{
    /// <summary>
    /// Encrypt a message for a recipient using their public key.
    /// No private key required - anyone can encrypt to a public key.
    /// </summary>
    /// <param name="message">The plaintext message</param>
    /// <param name="recipientPublicKey">The recipient's X25519 public key (Base64)</param>
    /// <returns>The encrypted message or error</returns>
    ValueTask<PrfResult<AsymmetricEncryptedData>> EncryptAsync(string message, string recipientPublicKey);

    /// <summary>
    /// Sign a message with Ed25519, then encrypt for a recipient (sign + encrypt).
    /// The recipient can decrypt and verify the sender's identity.
    /// </summary>
    /// <param name="message">The plaintext message</param>
    /// <param name="recipientPublicKey">The recipient's X25519 public key (Base64)</param>
    /// <param name="signingService">Signing service for Ed25519</param>
    /// <param name="senderEd25519PublicKey">Sender's Ed25519 public key (Base64)</param>
    /// <param name="keyIdentifier">Key identifier for signing (salt)</param>
    /// <returns>The signed and encrypted message or error</returns>
    ValueTask<PrfResult<AsymmetricEncryptedData>> SignAndEncryptAsync(
        string message,
        string recipientPublicKey,
        ISigningService signingService,
        string senderEd25519PublicKey,
        string keyIdentifier);

    /// <summary>
    /// Decrypt a message using the private key.
    /// </summary>
    /// <param name="asymmetricEncrypted">The encrypted message</param>
    /// <param name="keyIdentifier">Identifier for the key (salt for PRF, key ID for PseudoPRF)</param>
    /// <returns>The decrypted plaintext or error</returns>
    ValueTask<PrfResult<string>> DecryptAsync(AsymmetricEncryptedData asymmetricEncrypted, string keyIdentifier);

    /// <summary>
    /// Decrypt a message and verify the sender's signature if present.
    /// </summary>
    /// <param name="asymmetricEncrypted">The encrypted message (may include signature)</param>
    /// <param name="keyIdentifier">Identifier for the key (salt)</param>
    /// <param name="signingService">Signing service for Ed25519 verification</param>
    /// <returns>Decrypted result with verification status</returns>
    ValueTask<PrfResult<DecryptedData>> DecryptAndVerifyAsync(
        AsymmetricEncryptedData asymmetricEncrypted,
        string keyIdentifier,
        ISigningService signingService);
}
