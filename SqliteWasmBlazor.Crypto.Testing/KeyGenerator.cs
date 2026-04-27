using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Testing;

/// <summary>
/// Key derivation utilities for PRF-based key generation.
/// Supports both X25519 (encryption) and Ed25519 (signing) keys.
/// </summary>
public static class KeyGenerator
{
    // HKDF contexts for key separation - must match Noble.js implementation
    private static readonly byte[] X25519Context = "x25519-key"u8.ToArray();
    private static readonly byte[] Ed25519Context = "ed25519-key"u8.ToArray();
    /// <summary>
    /// Derives an X25519 keypair from a 32-byte PRF output.
    /// The PRF output is used directly as the private key.
    /// </summary>
    /// <param name="prfOutputBase64">32-byte PRF output (Base64)</param>
    /// <returns>KeyPair with private and public keys</returns>
    public static KeyPair DeriveKeypairFromPrf(string prfOutputBase64)
    {
        var prfOutput = Convert.FromBase64String(prfOutputBase64);
        if (prfOutput.Length != 32)
        {
            throw new ArgumentException("PRF output must be 32 bytes", nameof(prfOutputBase64));
        }

        return DeriveKeypairFromPrf(prfOutput);
    }

    /// <summary>
    /// Derives an X25519 keypair from a 32-byte PRF output.
    /// The PRF output is used directly as the private key.
    /// </summary>
    /// <param name="prfOutput">32-byte PRF output</param>
    /// <returns>KeyPair with private and public keys</returns>
    public static KeyPair DeriveKeypairFromPrf(byte[] prfOutput)
    {
        if (prfOutput.Length != 32)
        {
            throw new ArgumentException("PRF output must be 32 bytes", nameof(prfOutput));
        }

        // Use PRF output directly as private key
        // X25519 will apply clamping internally
        var privateKeyParams = new X25519PrivateKeyParameters(prfOutput, 0);
        var publicKeyParams = privateKeyParams.GeneratePublicKey();

        var privateKeyBytes = new byte[32];
        var publicKeyBytes = new byte[32];

        privateKeyParams.Encode(privateKeyBytes, 0);
        publicKeyParams.Encode(publicKeyBytes, 0);

        return new KeyPair(
            Convert.ToBase64String(privateKeyBytes),
            Convert.ToBase64String(publicKeyBytes)
        );
    }

    /// <summary>
    /// Generates a random X25519 keypair.
    /// </summary>
    public static KeyPair GenerateKeyPair()
    {
        var random = new SecureRandom();
        var generator = new X25519KeyPairGenerator();
        generator.Init(new X25519KeyGenerationParameters(random));

        var keyPair = generator.GenerateKeyPair();
        var privateKey = (X25519PrivateKeyParameters)keyPair.Private;
        var publicKey = (X25519PublicKeyParameters)keyPair.Public;

        var privateKeyBytes = new byte[32];
        var publicKeyBytes = new byte[32];

        privateKey.Encode(privateKeyBytes, 0);
        publicKey.Encode(publicKeyBytes, 0);

        return new KeyPair(
            Convert.ToBase64String(privateKeyBytes),
            Convert.ToBase64String(publicKeyBytes)
        );
    }

    /// <summary>
    /// Gets the public key from a private key.
    /// </summary>
    public static string GetPublicKey(string privateKeyBase64)
    {
        var privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
        if (privateKeyBytes.Length != 32)
        {
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKeyBase64));
        }

        var privateKey = new X25519PrivateKeyParameters(privateKeyBytes, 0);
        var publicKey = privateKey.GeneratePublicKey();

        var publicKeyBytes = new byte[32];
        publicKey.Encode(publicKeyBytes, 0);

        return Convert.ToBase64String(publicKeyBytes);
    }

    /// <summary>
    /// Validates that a Base64 string is a valid X25519 public key.
    /// </summary>
    /// <param name="publicKeyBase64">The public key to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidPublicKey(string publicKeyBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(publicKeyBase64);
            if (bytes.Length != 32)
            {
                return false;
            }

            // Try to import as public key
            _ = new X25519PublicKeyParameters(bytes, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that a Base64 string is a valid X25519 private key.
    /// </summary>
    /// <param name="privateKeyBase64">The private key to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidPrivateKey(string privateKeyBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(privateKeyBase64);
            if (bytes.Length != 32)
            {
                return false;
            }

            // Try to import as private key
            _ = new X25519PrivateKeyParameters(bytes, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a cryptographically secure random salt.
    /// </summary>
    /// <param name="length">Length in bytes (default 32)</param>
    /// <returns>Base64-encoded random salt</returns>
    public static string GenerateSalt(int length = 32)
    {
        var salt = new byte[length];
        var random = new SecureRandom();
        random.NextBytes(salt);
        return Convert.ToBase64String(salt);
    }

    // ============================================================
    // ED25519 SIGNING KEYS
    // ============================================================

    /// <summary>
    /// Derives an Ed25519 signing keypair from a 32-byte PRF seed.
    /// Uses HKDF with a different context than X25519 for key separation.
    /// </summary>
    /// <param name="prfSeed">32-byte PRF seed</param>
    /// <returns>KeyPair with Ed25519 private (seed) and public keys</returns>
    public static KeyPair DeriveEd25519KeyPair(byte[] prfSeed)
    {
        if (prfSeed.Length != 32)
        {
            throw new ArgumentException("PRF seed must be 32 bytes", nameof(prfSeed));
        }

        // Derive 32-byte seed for Ed25519 using HKDF with different context
        var ed25519Seed = HkdfDeriveKey(prfSeed, null, Ed25519Context, 32);

        return GenerateEd25519FromSeed(ed25519Seed);
    }

    /// <summary>
    /// Derives an Ed25519 signing keypair from a Base64-encoded PRF seed.
    /// </summary>
    /// <param name="prfSeedBase64">Base64-encoded 32-byte PRF seed</param>
    /// <returns>KeyPair with Ed25519 private (seed) and public keys</returns>
    public static KeyPair DeriveEd25519KeyPair(string prfSeedBase64)
    {
        var prfSeed = Convert.FromBase64String(prfSeedBase64);
        return DeriveEd25519KeyPair(prfSeed);
    }

    /// <summary>
    /// Generates an Ed25519 keypair from a 32-byte seed.
    /// </summary>
    /// <param name="seed">32-byte seed</param>
    /// <returns>KeyPair with private (seed) and public keys</returns>
    public static KeyPair GenerateEd25519FromSeed(byte[] seed)
    {
        if (seed.Length != 32)
        {
            throw new ArgumentException("Seed must be 32 bytes", nameof(seed));
        }

        var privateKeyParams = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKeyParams = privateKeyParams.GeneratePublicKey();

        return new KeyPair(
            Convert.ToBase64String(privateKeyParams.GetEncoded()),
            Convert.ToBase64String(publicKeyParams.GetEncoded())
        );
    }

    /// <summary>
    /// Generates a random Ed25519 keypair.
    /// </summary>
    /// <returns>KeyPair with Ed25519 private (seed) and public keys</returns>
    public static KeyPair GenerateEd25519KeyPair()
    {
        var random = new SecureRandom();
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(random));

        var keyPair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)keyPair.Private;
        var publicKey = (Ed25519PublicKeyParameters)keyPair.Public;

        return new KeyPair(
            Convert.ToBase64String(privateKey.GetEncoded()),
            Convert.ToBase64String(publicKey.GetEncoded())
        );
    }

    /// <summary>
    /// Gets the Ed25519 public key from a private key (seed).
    /// </summary>
    /// <param name="privateKeyBase64">Base64-encoded Ed25519 private key (32-byte seed)</param>
    /// <returns>Base64-encoded Ed25519 public key</returns>
    public static string GetEd25519PublicKey(string privateKeyBase64)
    {
        var privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
        if (privateKeyBytes.Length != 32)
        {
            throw new ArgumentException("Ed25519 private key must be 32 bytes", nameof(privateKeyBase64));
        }

        var privateKey = new Ed25519PrivateKeyParameters(privateKeyBytes, 0);
        var publicKey = privateKey.GeneratePublicKey();

        return Convert.ToBase64String(publicKey.GetEncoded());
    }

    /// <summary>
    /// Validates that a Base64 string is a valid Ed25519 public key.
    /// </summary>
    /// <param name="publicKeyBase64">The public key to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidEd25519PublicKey(string publicKeyBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(publicKeyBase64);
            if (bytes.Length != 32)
            {
                return false;
            }

            _ = new Ed25519PublicKeyParameters(bytes, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that a Base64 string is a valid Ed25519 private key (seed).
    /// </summary>
    /// <param name="privateKeyBase64">The private key to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidEd25519PrivateKey(string privateKeyBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(privateKeyBase64);
            if (bytes.Length != 32)
            {
                return false;
            }

            _ = new Ed25519PrivateKeyParameters(bytes, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // DUAL KEY DERIVATION (X25519 + Ed25519)
    // ============================================================

    /// <summary>
    /// Derives both X25519 (encryption) and Ed25519 (signing) keypairs from a single PRF seed.
    /// Uses different HKDF contexts to ensure key separation.
    /// </summary>
    /// <param name="prfSeed">32-byte PRF seed</param>
    /// <returns>DualKeyPairFull with both X25519 and Ed25519 key pairs</returns>
    public static DualKeyPairFull DeriveDualKeyPair(byte[] prfSeed)
    {
        if (prfSeed.Length != 32)
        {
            throw new ArgumentException("PRF seed must be 32 bytes", nameof(prfSeed));
        }

        // Derive X25519 key using HKDF
        var x25519Seed = HkdfDeriveKey(prfSeed, null, X25519Context, 32);
        var x25519KeyPair = DeriveKeypairFromPrf(x25519Seed);

        // Derive Ed25519 key using HKDF with different context
        var ed25519KeyPair = DeriveEd25519KeyPair(prfSeed);

        return new DualKeyPairFull(
            X25519PrivateKey: x25519KeyPair.PrivateKeyBase64,
            X25519PublicKey: x25519KeyPair.PublicKeyBase64,
            Ed25519PrivateKey: ed25519KeyPair.PrivateKeyBase64,
            Ed25519PublicKey: ed25519KeyPair.PublicKeyBase64
        );
    }

    /// <summary>
    /// Derives both X25519 and Ed25519 keypairs from a Base64-encoded PRF seed.
    /// </summary>
    /// <param name="prfSeedBase64">Base64-encoded 32-byte PRF seed</param>
    /// <returns>DualKeyPairFull with both X25519 and Ed25519 key pairs</returns>
    public static DualKeyPairFull DeriveDualKeyPair(string prfSeedBase64)
    {
        var prfSeed = Convert.FromBase64String(prfSeedBase64);
        return DeriveDualKeyPair(prfSeed);
    }

    // ============================================================
    // DOMAIN-SPECIFIC KEY DERIVATION
    // ============================================================

    /// <summary>
    /// Derives a domain-specific symmetric key from a PRF seed.
    /// Uses HKDF with a domain-specific context to ensure key separation.
    /// This allows multiple application domains (contacts, invitations, etc.)
    /// to have unique keys derived from a single WebAuthn authentication.
    /// </summary>
    /// <param name="prfSeed">32-byte PRF seed from WebAuthn</param>
    /// <param name="domain">Domain identifier (e.g., "contacts-user-data", "invitation-email")</param>
    /// <returns>32-byte domain-specific symmetric key</returns>
    public static byte[] DeriveDomainKey(byte[] prfSeed, string domain)
    {
        if (prfSeed.Length != 32)
        {
            throw new ArgumentException("PRF seed must be 32 bytes", nameof(prfSeed));
        }

        ArgumentException.ThrowIfNullOrEmpty(domain);

        // Create domain-specific context for HKDF
        var domainContext = System.Text.Encoding.UTF8.GetBytes($"symmetric-key:{domain}");

        return HkdfDeriveKey(prfSeed, null, domainContext, 32);
    }

    /// <summary>
    /// Derives a domain-specific symmetric key from a Base64-encoded PRF seed.
    /// </summary>
    /// <param name="prfSeedBase64">Base64-encoded 32-byte PRF seed</param>
    /// <param name="domain">Domain identifier</param>
    /// <returns>Base64-encoded 32-byte domain-specific symmetric key</returns>
    public static string DeriveDomainKey(string prfSeedBase64, string domain)
    {
        var prfSeed = Convert.FromBase64String(prfSeedBase64);
        var domainKey = DeriveDomainKey(prfSeed, domain);
        return Convert.ToBase64String(domainKey);
    }

    // ============================================================
    // PRIVATE HELPERS
    // ============================================================

    /// <summary>
    /// HKDF key derivation using BouncyCastle (WASM-compatible).
    /// System.Security.Cryptography.HKDF is not supported in Blazor WebAssembly.
    /// </summary>
    /// <remarks>
    /// When salt is null, uses 32 zero bytes (SHA-256 output length) to match Noble.js behavior.
    /// This is per RFC 5869 which specifies salt defaults to HashLen zeros if not provided.
    /// </remarks>
    internal static byte[] HkdfDeriveKey(byte[] ikm, byte[]? salt, byte[]? info, int outputLength)
    {
        var hkdf = new HkdfBytesGenerator(new Sha256Digest());
        // Per RFC 5869: if salt is not provided, use HashLen zeros (32 bytes for SHA-256)
        // This matches Noble.js behavior where undefined salt becomes new Uint8Array(32)
        var effectiveSalt = salt ?? new byte[32];
        var hkdfParams = new HkdfParameters(ikm, effectiveSalt, info);
        hkdf.Init(hkdfParams);

        var output = new byte[outputLength];
        hkdf.GenerateBytes(output, 0, outputLength);
        return output;
    }
}
