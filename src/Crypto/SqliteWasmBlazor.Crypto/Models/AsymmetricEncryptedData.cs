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
/// Represents a symmetric encrypted message using AES-256-GCM.
/// </summary>
/// <param name="Ciphertext">The encrypted ciphertext with auth tag (Base64).</param>
/// <param name="Nonce">The encryption nonce (Base64).</param>
public sealed record SymmetricEncryptedData(
    string Ciphertext,
    string Nonce
);
