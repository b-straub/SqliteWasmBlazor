using System.Security.Cryptography;

namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Contains both encryption (X25519) and signing (Ed25519) public keys
/// derived from the same PRF seed using different HKDF contexts.
/// </summary>
/// <param name="X25519PublicKey">Base64-encoded X25519 public key for ECIES encryption.</param>
/// <param name="Ed25519PublicKey">Base64-encoded Ed25519 public key for digital signatures.</param>
public sealed record DualKeyPair(
    string X25519PublicKey,
    string Ed25519PublicKey
);

/// <summary>
/// Contains both encryption (X25519) and signing (Ed25519) key pairs (private + public).
/// Private keys are exposed as raw <see cref="byte"/>[] so callers can ZeroMemory them
/// at end-of-life (P21 — secret key material never lands in <see cref="System.String"/>).
/// </summary>
/// <param name="X25519PrivateKey">Raw 32-byte X25519 private key. Caller-owned; clear via <see cref="Clear"/>.</param>
/// <param name="X25519PublicKey">Base64-encoded X25519 public key (wire-shaped, not secret).</param>
/// <param name="Ed25519PrivateKey">Raw 32-byte Ed25519 private key (seed). Caller-owned; clear via <see cref="Clear"/>.</param>
/// <param name="Ed25519PublicKey">Base64-encoded Ed25519 public key (wire-shaped, not secret).</param>
public sealed record DualKeyPairFull(
    byte[] X25519PrivateKey,
    string X25519PublicKey,
    byte[] Ed25519PrivateKey,
    string Ed25519PublicKey
)
{
    /// <summary>
    /// Gets just the public keys.
    /// </summary>
    public DualKeyPair PublicKeys => new(X25519PublicKey, Ed25519PublicKey);

    /// <summary>
    /// Zeroes both private-key byte arrays. Callers handling secret material
    /// should invoke this in <c>finally</c>.
    /// </summary>
    public void Clear()
    {
        CryptographicOperations.ZeroMemory(X25519PrivateKey);
        CryptographicOperations.ZeroMemory(Ed25519PrivateKey);
    }
}
