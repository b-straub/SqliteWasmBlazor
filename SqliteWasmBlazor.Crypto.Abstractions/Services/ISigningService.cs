using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Abstractions.Services;

/// <summary>
/// Service for Ed25519 digital signatures.
/// </summary>
public interface ISigningService
{
    /// <summary>
    /// Sign a message using the Ed25519 private key.
    /// </summary>
    /// <param name="message">The message to sign</param>
    /// <param name="keyIdentifier">Identifier for the key (salt for PRF, key ID for PseudoPRF)</param>
    /// <returns>The Base64-encoded signature or error</returns>
    ValueTask<PrfResult<string>> SignAsync(string message, string keyIdentifier);

    /// <summary>
    /// Verify a signature using the Ed25519 public key.
    /// </summary>
    /// <param name="message">The original message</param>
    /// <param name="signature">The Base64-encoded signature</param>
    /// <param name="publicKey">The Ed25519 public key (Base64)</param>
    /// <returns>True if signature is valid, false otherwise</returns>
    ValueTask<bool> VerifyAsync(string message, string signature, string publicKey);

    /// <summary>
    /// Create a signed message with timestamp for replay protection.
    /// </summary>
    /// <param name="message">The message to sign</param>
    /// <param name="keyIdentifier">Identifier for the key</param>
    /// <returns>The signed message with metadata or error</returns>
    ValueTask<PrfResult<SignedData>> CreateSignedMessageAsync(string message, string keyIdentifier);

    /// <summary>
    /// Verify a signed message including timestamp check.
    /// </summary>
    /// <param name="signedData">The signed message to verify</param>
    /// <param name="maxAgeSeconds">Maximum age of the message in seconds (default 300 = 5 minutes)</param>
    /// <returns>True if signature is valid and message is not expired</returns>
    ValueTask<bool> VerifySignedMessageAsync(SignedData signedData, int maxAgeSeconds = 300);
}
