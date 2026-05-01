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
/// </summary>
/// <param name="X25519PrivateKey">Base64-encoded X25519 private key.</param>
/// <param name="X25519PublicKey">Base64-encoded X25519 public key.</param>
/// <param name="Ed25519PrivateKey">Base64-encoded Ed25519 private key (32-byte seed).</param>
/// <param name="Ed25519PublicKey">Base64-encoded Ed25519 public key.</param>
public sealed record DualKeyPairFull(
    string X25519PrivateKey,
    string X25519PublicKey,
    string Ed25519PrivateKey,
    string Ed25519PublicKey
)
{
    /// <summary>
    /// Gets just the public keys.
    /// </summary>
    public DualKeyPair PublicKeys => new(X25519PublicKey, Ed25519PublicKey);
}
