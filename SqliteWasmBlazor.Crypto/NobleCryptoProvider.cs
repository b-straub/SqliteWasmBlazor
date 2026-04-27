using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Interop;

namespace SqliteWasmBlazor.Crypto;

/// <summary>
/// Crypto provider using Noble.js + SubtleCrypto via packed binary Base64 bridge.
/// No JSON parsing — all interop uses Base64-encoded packed binary with fixed-size headers.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class NobleCryptoProvider : ICryptoProvider
{
    private const int NonceLength = 12;
    private const int KeyLength = 32;
    private const int SignatureLength = 64;
    private const int EphemeralKeyLength = 32;

    public string ProviderName => "Noble.js + SubtleCrypto";

    public NobleCryptoProvider(IOptions<SqliteWasmBlazorCryptoOptions> options)
    {
        var resolved = options.Value;
        // Configure-once for the static interop. Idempotent — see NobleInterop.Configure.
        NobleInterop.Configure(resolved.BaseHref, resolved.AssetRoot);
    }

    // ============================================================
    // SYMMETRIC ENCRYPTION (AES-256-GCM)
    // ============================================================

    public async ValueTask<PrfResult<SymmetricEncryptedData>> EncryptSymmetricAsync(
        string plaintext,
        ReadOnlyMemory<byte> key,
        string? associatedData = null)
    {
        await NobleInterop.EnsureInitializedAsync();

        var plaintextBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        var keyBase64 = Convert.ToBase64String(key.Span);

        var packedBase64 = await NobleInterop.EncryptAesGcmAsync(plaintextBase64, keyBase64, associatedData);
        var packed = Convert.FromBase64String(packedBase64);

        if (packed.Length <= NonceLength)
        {
            return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }

        return PrfResult<SymmetricEncryptedData>.Ok(UnpackSymmetricEncrypted(packed));
    }

    public async ValueTask<PrfResult<string>> DecryptSymmetricAsync(
        SymmetricEncryptedData encrypted,
        ReadOnlyMemory<byte> key,
        string? associatedData = null)
    {
        await NobleInterop.EnsureInitializedAsync();

        try
        {
            var keyBase64 = Convert.ToBase64String(key.Span);
            var packedBase64 = await NobleInterop.DecryptAesGcmAsync(encrypted.Ciphertext, encrypted.Nonce, keyBase64, associatedData);
            var plaintext = Convert.FromBase64String(packedBase64);

            if (plaintext.Length == 0)
            {
                return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }

            return PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    // ============================================================
    // ASYMMETRIC ENCRYPTION (ECIES: X25519 + AES-256-GCM)
    // ============================================================

    public async ValueTask<PrfResult<AsymmetricEncryptedData>> EncryptAsymmetricAsync(
        string plaintext,
        string recipientPublicKeyBase64)
    {
        await NobleInterop.EnsureInitializedAsync();

        var plaintextBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        var packedBase64 = await NobleInterop.EncryptAsymmetricAesGcmAsync(plaintextBase64, recipientPublicKeyBase64);
        var packed = Convert.FromBase64String(packedBase64);

        if (packed.Length <= EphemeralKeyLength + NonceLength)
        {
            return PrfResult<AsymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }

        // Unpack: [ephPubKey(32) | nonce(12) | ciphertext(N)]
        return PrfResult<AsymmetricEncryptedData>.Ok(new AsymmetricEncryptedData(
            Convert.ToBase64String(packed[..EphemeralKeyLength]),
            Convert.ToBase64String(packed[(EphemeralKeyLength + NonceLength)..]),
            Convert.ToBase64String(packed[EphemeralKeyLength..(EphemeralKeyLength + NonceLength)])
        ));
    }

    public async ValueTask<PrfResult<string>> DecryptAsymmetricAsync(
        AsymmetricEncryptedData asymmetricEncrypted,
        ReadOnlyMemory<byte> privateKey)
    {
        await NobleInterop.EnsureInitializedAsync();

        try
        {
            var privateKeyBase64 = Convert.ToBase64String(privateKey.Span);
            var packedBase64 = await NobleInterop.DecryptAsymmetricAesGcmAsync(
                asymmetricEncrypted.EphemeralPublicKey,
                asymmetricEncrypted.Ciphertext,
                asymmetricEncrypted.Nonce,
                privateKeyBase64);
            var plaintext = Convert.FromBase64String(packedBase64);

            if (plaintext.Length == 0)
            {
                return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }

            return PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    // ============================================================
    // ED25519 DIGITAL SIGNATURES
    // ============================================================

    public async ValueTask<PrfResult<string>> SignAsync(string message, ReadOnlyMemory<byte> privateKey)
    {
        await NobleInterop.EnsureInitializedAsync();

        var messageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        var privateKeyBase64 = Convert.ToBase64String(privateKey.Span);

        var signatureBase64 = NobleInterop.Ed25519Sign(messageBase64, privateKeyBase64);
        var signature = Convert.FromBase64String(signatureBase64);

        if (signature.Length != SignatureLength)
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }

        return PrfResult<string>.Ok(signatureBase64);
    }

    public async ValueTask<bool> VerifyAsync(string message, string signatureBase64, string publicKeyBase64)
    {
        await NobleInterop.EnsureInitializedAsync();

        var messageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        return NobleInterop.Ed25519Verify(signatureBase64, messageBase64, publicKeyBase64);
    }

    // ============================================================
    // KEY GENERATION
    // ============================================================

    public async ValueTask<KeyPair> DeriveX25519KeyPairAsync(ReadOnlyMemory<byte> prfSeed)
    {
        await NobleInterop.EnsureInitializedAsync();
        return UnpackKeyPair(NobleInterop.DeriveX25519KeyPair(Convert.ToBase64String(prfSeed.Span)));
    }

    public async ValueTask<KeyPair> DeriveEd25519KeyPairAsync(ReadOnlyMemory<byte> prfSeed)
    {
        await NobleInterop.EnsureInitializedAsync();
        return UnpackKeyPair(NobleInterop.DeriveEd25519KeyPair(Convert.ToBase64String(prfSeed.Span)));
    }

    public async ValueTask<DualKeyPairFull> DeriveDualKeyPairAsync(ReadOnlyMemory<byte> prfSeed)
    {
        await NobleInterop.EnsureInitializedAsync();
        var packed = Convert.FromBase64String(NobleInterop.DeriveDualKeyPair(Convert.ToBase64String(prfSeed.Span)));

        // Unpack: [x25519Priv(32) | x25519Pub(32) | ed25519Priv(32) | ed25519Pub(32)]
        return new DualKeyPairFull(
            Convert.ToBase64String(packed[..32]),
            Convert.ToBase64String(packed[32..64]),
            Convert.ToBase64String(packed[64..96]),
            Convert.ToBase64String(packed[96..128])
        );
    }

    public async ValueTask<string> GenerateSaltAsync(int length = 32)
    {
        await NobleInterop.EnsureInitializedAsync();
        return NobleInterop.GenerateRandomBytes(length);
    }

    // ============================================================
    // KEY-ID BASED OPERATIONS (Keys stay in JS)
    // ============================================================

    public bool SupportsKeyIdOperations => true;

    public async ValueTask<PrfResult<DualKeyPair>> StoreKeysAsync(string keyId, ReadOnlyMemory<byte> prfSeed, int? ttlMs)
    {
        await NobleInterop.EnsureInitializedAsync();

        var packedBase64 = await NobleInterop.StoreKeysAsync(keyId, Convert.ToBase64String(prfSeed.Span), ttlMs);
        var packed = Convert.FromBase64String(packedBase64);

        if (packed.Length != 64)
        {
            return PrfResult<DualKeyPair>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        return PrfResult<DualKeyPair>.Ok(new DualKeyPair(
            Convert.ToBase64String(packed[..32]),
            Convert.ToBase64String(packed[32..64])
        ));
    }

    public async ValueTask<PrfResult<DualKeyPair>> GetPublicKeysAsync(string keyId)
    {
        await NobleInterop.EnsureInitializedAsync();

        var packed = Convert.FromBase64String(NobleInterop.GetPublicKeys(keyId));

        if (packed.Length != 64)
        {
            return PrfResult<DualKeyPair>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        return PrfResult<DualKeyPair>.Ok(new DualKeyPair(
            Convert.ToBase64String(packed[..32]),
            Convert.ToBase64String(packed[32..64])
        ));
    }

    public bool HasCachedKey(string keyId) => NobleInterop.HasKey(keyId);

    public void RemoveCachedKey(string keyId) => NobleInterop.RemoveKeys(keyId);

    public async ValueTask<PrfResult<string>> SignWithKeyIdAsync(string message, string keyId)
    {
        await NobleInterop.EnsureInitializedAsync();

        var messageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        var signatureBase64 = await NobleInterop.SignWithCachedKeyAsync(keyId, messageBase64);
        var signature = Convert.FromBase64String(signatureBase64);

        if (signature.Length != SignatureLength)
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }

        return PrfResult<string>.Ok(signatureBase64);
    }

    public async ValueTask<PrfResult<SymmetricEncryptedData>> EncryptSymmetricWithKeyIdAsync(
        string plaintext, string keyId, string? associatedData = null)
    {
        await NobleInterop.EnsureInitializedAsync();

        var plaintextBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        var packedBase64 = await NobleInterop.EncryptSymmetricCachedAesGcmAsync(keyId, plaintextBase64, associatedData);
        var packed = Convert.FromBase64String(packedBase64);

        if (packed.Length <= NonceLength)
        {
            return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }

        return PrfResult<SymmetricEncryptedData>.Ok(UnpackSymmetricEncrypted(packed));
    }

    public async ValueTask<PrfResult<string>> DecryptSymmetricWithKeyIdAsync(
        SymmetricEncryptedData encrypted, string keyId, string? associatedData = null)
    {
        await NobleInterop.EnsureInitializedAsync();

        try
        {
            var packedBase64 = await NobleInterop.DecryptSymmetricCachedAesGcmAsync(
                keyId, encrypted.Ciphertext, encrypted.Nonce, associatedData);
            var plaintext = Convert.FromBase64String(packedBase64);

            if (plaintext.Length == 0)
            {
                return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }

            return PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    public async ValueTask<PrfResult<string>> DecryptAsymmetricWithKeyIdAsync(
        AsymmetricEncryptedData asymmetricEncrypted, string keyId)
    {
        await NobleInterop.EnsureInitializedAsync();

        try
        {
            var packedBase64 = await NobleInterop.DecryptAsymmetricCachedAesGcmAsync(
                keyId, asymmetricEncrypted.EphemeralPublicKey,
                asymmetricEncrypted.Ciphertext, asymmetricEncrypted.Nonce);
            var plaintext = Convert.FromBase64String(packedBase64);

            if (plaintext.Length == 0)
            {
                return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }

            return PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    // ============================================================
    // KEY WRAPPING & DERIVATION
    // ============================================================

    public async ValueTask<PrfResult<ReadOnlyMemory<byte>>> DeriveWrappingKeyAsync(
        ReadOnlyMemory<byte> ownPrivateKey, string recipientPublicKeyBase64, string context)
    {
        await NobleInterop.EnsureInitializedAsync();

        var wrappingKeyBase64 = NobleInterop.DeriveWrappingKey(
            Convert.ToBase64String(ownPrivateKey.Span), recipientPublicKeyBase64, context);
        var wrappingKey = Convert.FromBase64String(wrappingKeyBase64);

        if (wrappingKey.Length != KeyLength)
        {
            return PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        return PrfResult<ReadOnlyMemory<byte>>.Ok(wrappingKey);
    }

    public async ValueTask<ReadOnlyMemory<byte>> GenerateContentKeyAsync()
    {
        await NobleInterop.EnsureInitializedAsync();
        var keyBase64 = NobleInterop.GenerateRandomBytes(KeyLength);
        return Convert.FromBase64String(keyBase64);
    }

    public async ValueTask<PrfResult<SymmetricEncryptedData>> WrapContentKeyAsync(
        ReadOnlyMemory<byte> contentKey, ReadOnlyMemory<byte> wrappingKey)
    {
        await NobleInterop.EnsureInitializedAsync();

        var packedBase64 = await NobleInterop.EncryptAesGcmAsync(
            Convert.ToBase64String(contentKey.Span), Convert.ToBase64String(wrappingKey.Span));
        var packed = Convert.FromBase64String(packedBase64);

        if (packed.Length <= NonceLength)
        {
            return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }

        return PrfResult<SymmetricEncryptedData>.Ok(UnpackSymmetricEncrypted(packed));
    }

    public async ValueTask<PrfResult<ReadOnlyMemory<byte>>> UnwrapContentKeyAsync(
        SymmetricEncryptedData wrappedKey, ReadOnlyMemory<byte> wrappingKey)
    {
        await NobleInterop.EnsureInitializedAsync();

        try
        {
            var packedBase64 = await NobleInterop.DecryptAesGcmAsync(
                wrappedKey.Ciphertext, wrappedKey.Nonce, Convert.ToBase64String(wrappingKey.Span));
            var contentKey = Convert.FromBase64String(packedBase64);

            if (contentKey.Length == 0)
            {
                return PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }

            return PrfResult<ReadOnlyMemory<byte>>.Ok(contentKey);
        }
        catch
        {
            return PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH);
        }
    }

    // ============================================================
    // ADDITIONAL METHODS
    // ============================================================

    public async ValueTask<bool> IsSupportedAsync()
    {
        await NobleInterop.EnsureInitializedAsync();
        return NobleInterop.IsSupported();
    }

    public async ValueTask<KeyPair> GenerateX25519KeyPairAsync()
    {
        await NobleInterop.EnsureInitializedAsync();
        return UnpackKeyPair(NobleInterop.GenerateX25519KeyPair());
    }

    public async ValueTask<KeyPair> GenerateEd25519KeyPairAsync()
    {
        await NobleInterop.EnsureInitializedAsync();
        return UnpackKeyPair(NobleInterop.GenerateEd25519KeyPair());
    }

    public async ValueTask<string> GetX25519PublicKeyAsync(string privateKeyBase64)
    {
        await NobleInterop.EnsureInitializedAsync();
        return NobleInterop.GetX25519PublicKey(privateKeyBase64);
    }

    public async ValueTask<string> GetEd25519PublicKeyAsync(string privateKeyBase64)
    {
        await NobleInterop.EnsureInitializedAsync();
        return NobleInterop.GetEd25519PublicKey(privateKeyBase64);
    }

    // ============================================================
    // BINARY UNPACKING HELPERS
    // ============================================================

    /// <summary>
    /// Unpack [nonce(12) | ciphertext(N)] into SymmetricEncryptedData with Base64 fields.
    /// </summary>
    private static SymmetricEncryptedData UnpackSymmetricEncrypted(byte[] packed)
    {
        return new SymmetricEncryptedData(
            Convert.ToBase64String(packed[NonceLength..]),
            Convert.ToBase64String(packed[..NonceLength])
        );
    }

    /// <summary>
    /// Unpack Base64-encoded [privKey(32) | pubKey(32)] into KeyPair.
    /// </summary>
    private static KeyPair UnpackKeyPair(string packedBase64)
    {
        var packed = Convert.FromBase64String(packedBase64);
        return new KeyPair(
            Convert.ToBase64String(packed[..KeyLength]),
            Convert.ToBase64String(packed[KeyLength..(KeyLength * 2)])
        );
    }
}
