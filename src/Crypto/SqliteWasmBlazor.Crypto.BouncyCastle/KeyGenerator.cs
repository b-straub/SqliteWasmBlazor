using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.BouncyCastle;

/// <summary>
/// Key derivation utilities for PRF-based key generation.
/// Two callers only — both inside <see cref="BouncyCastleCryptoProvider"/>:
/// <see cref="HkdfDeriveKey"/> backs every ECIES / wrap-key derivation,
/// <see cref="DeriveDualKeyPair"/> backs <see cref="ICryptoProvider.DeriveDualKeyPairAsync"/>.
/// </summary>
public static class KeyGenerator
{
    // HKDF contexts for key separation — must match the JS-side crypto-core derivation.
    private static readonly byte[] X25519Context = "x25519-key"u8.ToArray();
    private static readonly byte[] Ed25519Context = "ed25519-key"u8.ToArray();

    /// <summary>
    /// Derives both X25519 (encryption) and Ed25519 (signing) keypairs from a
    /// single 32-byte PRF seed. X25519 uses HKDF over the seed with the
    /// <c>x25519-key</c> context; Ed25519 reuses the seed directly via HKDF
    /// with the <c>ed25519-key</c> context — matching the JS-side crypto-core derivation.
    /// </summary>
    /// <remarks>
    /// The HKDF-derived intermediate seed for X25519 is zeroed after the
    /// private-key bytes are extracted. The Ed25519 seed buffer is moved
    /// into the result record without copying (caller is responsible for
    /// <see cref="DualKeyPairFull.Clear"/> at end-of-life — P21).
    /// </remarks>
    /// <param name="prfSeed">32-byte PRF seed (caller-owned, caller-zeroed).</param>
    public static DualKeyPairFull DeriveDualKeyPair(byte[] prfSeed)
    {
        if (prfSeed.Length != 32)
        {
            throw new ArgumentException("PRF seed must be 32 bytes", nameof(prfSeed));
        }

        var x25519Seed = HkdfDeriveKey(prfSeed, null, X25519Context, 32);
        byte[] x25519PrivBytes;
        byte[] x25519PubBytes;
        try
        {
            var x25519Priv = new X25519PrivateKeyParameters(x25519Seed, 0);
            var x25519Pub = x25519Priv.GeneratePublicKey();
            x25519PrivBytes = new byte[32];
            x25519PubBytes = new byte[32];
            x25519Priv.Encode(x25519PrivBytes, 0);
            x25519Pub.Encode(x25519PubBytes, 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x25519Seed);
        }

        var ed25519Seed = HkdfDeriveKey(prfSeed, null, Ed25519Context, 32);
        var ed25519Priv = new Ed25519PrivateKeyParameters(ed25519Seed, 0);
        var ed25519Pub = ed25519Priv.GeneratePublicKey();
        var ed25519PrivBytes = ed25519Priv.GetEncoded();
        var ed25519PubBytes = ed25519Pub.GetEncoded();
        CryptographicOperations.ZeroMemory(ed25519Seed);

        return new DualKeyPairFull(
            X25519PrivateKey: x25519PrivBytes,
            X25519PublicKey: Convert.ToBase64String(x25519PubBytes),
            Ed25519PrivateKey: ed25519PrivBytes,
            Ed25519PublicKey: Convert.ToBase64String(ed25519PubBytes)
        );
    }

    /// <summary>
    /// HKDF key derivation using BouncyCastle (WASM-compatible).
    /// <c>System.Security.Cryptography.HKDF</c> is not supported in Blazor WebAssembly.
    /// </summary>
    /// <remarks>
    /// When salt is null, uses 32 zero bytes (SHA-256 output length) per RFC 5869,
    /// which specifies salt defaults to HashLen zeros if not provided.
    /// </remarks>
    internal static byte[] HkdfDeriveKey(byte[] ikm, byte[]? salt, byte[]? info, int outputLength)
    {
        var hkdf = new HkdfBytesGenerator(new Sha256Digest());
        // Per RFC 5869: if salt is not provided, use HashLen zeros (32 bytes for SHA-256).
        var effectiveSalt = salt ?? new byte[32];
        var hkdfParams = new HkdfParameters(ikm, effectiveSalt, info);
        hkdf.Init(hkdfParams);

        var output = new byte[outputLength];
        hkdf.GenerateBytes(output, 0, outputLength);
        return output;
    }
}
