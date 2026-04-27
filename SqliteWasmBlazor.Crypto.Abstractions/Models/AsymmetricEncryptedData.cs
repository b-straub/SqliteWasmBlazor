namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Represents an ECIES encrypted message using X25519 + AES-256-GCM.
/// </summary>
/// <param name="EphemeralPublicKey">The ephemeral X25519 public key used for ECDH (Base64, 32 bytes).</param>
/// <param name="Ciphertext">The encrypted ciphertext with auth tag (Base64).</param>
/// <param name="Nonce">The encryption nonce (Base64).</param>
public sealed record AsymmetricEncryptedData(
    string EphemeralPublicKey,
    string Ciphertext,
    string Nonce
);

/// <summary>
/// Inner envelope for sign+encrypt: signed plaintext bundled with sender identity.
/// This entire object is encrypted — signature cannot be stripped or substituted.
/// </summary>
/// <param name="Message">The original plaintext message.</param>
/// <param name="Signature">Ed25519 signature of Message (Base64).</param>
/// <param name="SenderEd25519PublicKey">Sender's Ed25519 public key (Base64).</param>
public sealed record SignedEnvelope(
    string Message,
    string Signature,
    string SenderEd25519PublicKey
);

/// <summary>
/// Result of decrypting a message that may include sender verification.
/// </summary>
/// <param name="Plaintext">The decrypted plaintext.</param>
/// <param name="SenderEd25519PublicKey">The sender's Ed25519 public key, if the message was signed.</param>
/// <param name="SignatureValid">Whether the sender's signature was verified. null = message was not signed.</param>
public sealed record DecryptedData(
    string Plaintext,
    string? SenderEd25519PublicKey = null,
    bool? SignatureValid = null
)
{
    /// <summary>
    /// Whether the message was signed and the signature is valid.
    /// </summary>
    public bool IsVerified => SignatureValid == true;

    /// <summary>
    /// Whether the message was signed but the signature is invalid.
    /// </summary>
    public bool IsSignatureInvalid => SignatureValid == false;
}

/// <summary>
/// Represents a symmetric encrypted message using AES-256-GCM.
/// </summary>
/// <param name="Ciphertext">The encrypted ciphertext with auth tag (Base64).</param>
/// <param name="Nonce">The encryption nonce (Base64).</param>
public sealed record SymmetricEncryptedData(
    string Ciphertext,
    string Nonce
);
