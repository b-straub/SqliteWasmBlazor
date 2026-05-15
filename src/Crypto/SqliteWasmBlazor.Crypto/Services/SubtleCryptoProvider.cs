using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Interop;

namespace SqliteWasmBlazor.Crypto;

/// <summary>
/// Crypto provider using SubtleCrypto + @awasm/noble via packed binary Base64 bridge.
/// No JSON parsing — all interop uses Base64-encoded packed binary with fixed-size headers.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class SubtleCryptoProvider : ICryptoProvider
{
    private const int NonceLength = 12;
    private const int KeyLength = 32;
    private const int SignatureLength = 64;
    private const int EphemeralKeyLength = 32;

    public string ProviderName => "SubtleCrypto + @awasm/noble";

    public SubtleCryptoProvider(IOptions<SqliteWasmBlazorCryptoOptions> options)
    {
        var resolved = options.Value;
        // Configure-once for the static interop. Idempotent — see CryptoInterop.Configure.
        CryptoInterop.Configure(resolved.BaseHref, resolved.AssetRoot);
    }

    // ============================================================
    // SYMMETRIC ENCRYPTION (AES-256-GCM)
    // ============================================================

    public async ValueTask<PrfResult<SymmetricEncryptedData>> EncryptSymmetricAsync(
        string plaintext,
        ReadOnlyMemory<byte> key,
        string? associatedData = null)
    {
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(key, out ArraySegment<byte> keySegment))
        {
            return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        var packedBase64 = await CryptoInterop.EncryptAesGcmAsync(
            new ArraySegment<byte>(plaintextBytes), keySegment, associatedData);
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
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(key, out ArraySegment<byte> keySegment))
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }

        try
        {
            var packedBase64 = await CryptoInterop.DecryptAesGcmAsync(encrypted.Ciphertext, encrypted.Nonce, keySegment, associatedData);
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
        await CryptoInterop.EnsureInitializedAsync();

        var plaintextBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        var packedBase64 = await CryptoInterop.EncryptAsymmetricAesGcmAsync(plaintextBase64, recipientPublicKeyBase64);
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

    public async ValueTask<PrfResult<AsymmetricEncryptedData>> EncryptAsymmetricFromBytesAsync(
        ReadOnlyMemory<byte> plaintext,
        string recipientPublicKeyBase64)
    {
        await CryptoInterop.EnsureInitializedAsync();

        // BridgeAsync hides MemoryMarshal.TryGetArray + try/catch boilerplate
        // and gives every byte-shaped call site the same call shape (P21
        // canonical pattern). Output is Base64 of [eph(32)|nonce(12)|ct]
        // which is not secret-bearing on its own.
        var packedResult = await BridgeAsync.BytesInBase64Out(
            plaintext,
            jsCall: pt => CryptoInterop.EncryptAsymmetricFromBytesAesGcmAsync(pt, recipientPublicKeyBase64),
            failureCode: PrfErrorCode.ENCRYPTION_FAILED);

        if (!packedResult.Success || packedResult.Value is null)
        {
            return PrfResult<AsymmetricEncryptedData>.Fail(packedResult.ErrorCode ?? PrfErrorCode.ENCRYPTION_FAILED);
        }

        var packed = Convert.FromBase64String(packedResult.Value);
        if (packed.Length <= EphemeralKeyLength + NonceLength)
        {
            return PrfResult<AsymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }

        return PrfResult<AsymmetricEncryptedData>.Ok(new AsymmetricEncryptedData(
            Convert.ToBase64String(packed[..EphemeralKeyLength]),
            Convert.ToBase64String(packed[(EphemeralKeyLength + NonceLength)..]),
            Convert.ToBase64String(packed[EphemeralKeyLength..(EphemeralKeyLength + NonceLength)])
        ));
    }

    public async ValueTask<PrfResult<byte[]>> DecryptAsymmetricToBytesAsync(
        AsymmetricEncryptedData asymmetricEncrypted,
        ReadOnlyMemory<byte> privateKey)
    {
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(privateKey, out ArraySegment<byte> privKeySegment))
        {
            return PrfResult<byte[]>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }

        try
        {
            // Bytes-in / bytes-out canonical shape (P21). Plaintext length
            // is ciphertext bytes − 16-byte AES-GCM tag. JS writes the
            // plaintext directly into the caller-allocated buffer — no
            // Base64 string carrying K_wrap on either heap.
            var plaintextLength = GetBase64DecodedLength(asymmetricEncrypted.Ciphertext) - 16;
            if (plaintextLength <= 0)
            {
                return PrfResult<byte[]>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }
            var plaintext = new byte[plaintextLength];
            await CryptoInterop.DecryptAsymmetricIntoAsync(
                asymmetricEncrypted.EphemeralPublicKey,
                asymmetricEncrypted.Ciphertext,
                asymmetricEncrypted.Nonce,
                privKeySegment,
                new ArraySegment<byte>(plaintext));
            return PrfResult<byte[]>.Ok(plaintext);
        }
        catch
        {
            return PrfResult<byte[]>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    // ============================================================
    // ED25519 DIGITAL SIGNATURES
    // ============================================================

    public async ValueTask<PrfResult<string>> SignAsync(string message, ReadOnlyMemory<byte> privateKey)
    {
        await CryptoInterop.EnsureInitializedAsync();

        var messageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));

        // Cross to JS as a binary MemoryView — no immutable Base64 string ever
        // holds the private-key bytes on the JS heap. The caller-owned byte[]
        // is exposed directly via MemoryMarshal.TryGetArray; no managed copy
        // of the secret is allocated here. Caller (SigningService) zeros its
        // byte[] in finally.
        if (!MemoryMarshal.TryGetArray(privateKey, out ArraySegment<byte> privateKeySegment))
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }

        var signatureBase64 = await CryptoInterop.Ed25519SignAsync(messageBase64, privateKeySegment);

        var signature = Convert.FromBase64String(signatureBase64);
        if (signature.Length != SignatureLength)
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }

        return PrfResult<string>.Ok(signatureBase64);
    }

    public async ValueTask<bool> VerifyAsync(string message, string signatureBase64, string publicKeyBase64)
    {
        await CryptoInterop.EnsureInitializedAsync();

        var messageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        return await CryptoInterop.Ed25519VerifyAsync(signatureBase64, messageBase64, publicKeyBase64);
    }

    // ============================================================
    // KEY GENERATION
    // ============================================================

    public async ValueTask<DualKeyPairFull> DeriveDualKeyPairAsync(ReadOnlyMemory<byte> prfSeed)
    {
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(prfSeed, out ArraySegment<byte> seedSegment))
        {
            throw new InvalidOperationException(
                "DeriveDualKeyPairAsync: caller-supplied seed must back onto an array (use byte[] or ReadOnlyMemory<byte> over byte[]).");
        }

        // JS writes [x25519Priv(32)|x25519Pub(32)|ed25519Priv(32)|ed25519Pub(32)]
        // directly into the packed buffer — no Base64 string on either heap.
        var packed = new byte[128];
        try
        {
            await CryptoInterop.DeriveDualKeyPairIntoAsync(seedSegment, packed);

            // Priv keys move into the record as fresh byte[]; caller owns the
            // record and invokes DualKeyPairFull.Clear() at end-of-life (P21).
            var x25519Priv = packed.AsSpan(0, 32).ToArray();
            var ed25519Priv = packed.AsSpan(64, 32).ToArray();
            return new DualKeyPairFull(
                X25519PrivateKey: x25519Priv,
                X25519PublicKey: Convert.ToBase64String(packed.AsSpan(32, 32)),
                Ed25519PrivateKey: ed25519Priv,
                Ed25519PublicKey: Convert.ToBase64String(packed.AsSpan(96, 32))
            );
        }
        finally
        {
            // Packed buffer holds raw private-key material written by the JS
            // bridge; clear it after extracting into the record fields.
            CryptographicOperations.ZeroMemory(packed);
        }
    }

    // ============================================================
    // KEY-ID BASED OPERATIONS (Keys stay in JS)
    // ============================================================

    public bool SupportsKeyIdOperations => true;

    public async ValueTask<PrfResult<DualKeyPair>> StoreKeysAsync(string keyId, ReadOnlyMemory<byte> prfSeed, int? ttlMs)
    {
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(prfSeed, out ArraySegment<byte> seedSegment))
        {
            return PrfResult<DualKeyPair>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        var packedBase64 = await CryptoInterop.StoreKeysAsync(keyId, seedSegment, ttlMs);
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
        await CryptoInterop.EnsureInitializedAsync();

        var packed = Convert.FromBase64String(CryptoInterop.GetPublicKeys(keyId));

        if (packed.Length != 64)
        {
            return PrfResult<DualKeyPair>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        return PrfResult<DualKeyPair>.Ok(new DualKeyPair(
            Convert.ToBase64String(packed[..32]),
            Convert.ToBase64String(packed[32..64])
        ));
    }

    // Sync paths must guard on CryptoInterop.IsInitialized: the underlying
    // [JSImport]-bound calls assert the JS module is loaded and abort the
    // WASM runtime if not — fatal during first-render evaluations that race
    // the async EnsureInitializedAsync.
    public bool HasCachedKey(string keyId) =>
        CryptoInterop.IsInitialized && CryptoInterop.HasKey(keyId);

    public void RemoveCachedKey(string keyId)
    {
        if (!CryptoInterop.IsInitialized)
        {
            return;
        }
        CryptoInterop.RemoveKeys(keyId);
    }

    public async ValueTask<PrfResult<string>> SignWithKeyIdAsync(string message, string keyId)
    {
        await CryptoInterop.EnsureInitializedAsync();

        var messageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        var signatureBase64 = await CryptoInterop.SignWithCachedKeyAsync(keyId, messageBase64);
        var signature = Convert.FromBase64String(signatureBase64);

        if (signature.Length != SignatureLength)
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }

        return PrfResult<string>.Ok(signatureBase64);
    }

    public async ValueTask<PrfResult<string>> DecryptAsymmetricWithKeyIdAsync(
        AsymmetricEncryptedData asymmetricEncrypted, string keyId)
    {
        await CryptoInterop.EnsureInitializedAsync();

        try
        {
            var packedBase64 = await CryptoInterop.DecryptAsymmetricCachedAesGcmAsync(
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

    public async ValueTask<PrfResult<byte[]>> DecryptAsymmetricWithKeyIdToBytesAsync(
        AsymmetricEncryptedData asymmetricEncrypted, string keyId)
    {
        await CryptoInterop.EnsureInitializedAsync();

        byte[]? plaintext = null;
        try
        {
            var plaintextLength = GetBase64DecodedLength(asymmetricEncrypted.Ciphertext) - 16;
            if (plaintextLength <= 0)
            {
                return PrfResult<byte[]>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }

            plaintext = new byte[plaintextLength];
            await CryptoInterop.DecryptAsymmetricCachedIntoAsync(
                keyId,
                asymmetricEncrypted.EphemeralPublicKey,
                asymmetricEncrypted.Ciphertext,
                asymmetricEncrypted.Nonce,
                new ArraySegment<byte>(plaintext));

            var result = plaintext;
            plaintext = null;
            return PrfResult<byte[]>.Ok(result);
        }
        catch
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            return PrfResult<byte[]>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    // ============================================================
    // KEY WRAPPING & DERIVATION
    // ============================================================

    public async ValueTask<PrfResult<ReadOnlyMemory<byte>>> DeriveWrappingKeyAsync(
        ReadOnlyMemory<byte> ownPrivateKey, string recipientPublicKeyBase64, string context)
    {
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(ownPrivateKey, out ArraySegment<byte> ownPrivateKeySegment))
        {
            return PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        // Bytes-out via writable MemoryView output. JS writes the 32-byte
        // wrapping key directly into this buffer — no Base64 string carrying
        // the secret on the JS heap or the C# managed heap.
        var wrappingKey = new byte[KeyLength];
        await CryptoInterop.DeriveWrappingKeyIntoAsync(
            ownPrivateKeySegment,
            recipientPublicKeyBase64,
            context,
            wrappingKey);

        return PrfResult<ReadOnlyMemory<byte>>.Ok(wrappingKey);
    }

    public async ValueTask<ReadOnlyMemory<byte>> GenerateContentKeyAsync()
    {
        await CryptoInterop.EnsureInitializedAsync();
        // JS writes 32 secure random bytes directly into the output buffer —
        // no Base64 string carrying the CEK on either heap (P21).
        var key = new byte[KeyLength];
        CryptoInterop.GenerateRandomBytesInto(key.AsSpan());
        return key;
    }

    public async ValueTask<PrfResult<SymmetricEncryptedData>> WrapContentKeyAsync(
        ReadOnlyMemory<byte> contentKey, ReadOnlyMemory<byte> wrappingKey)
    {
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(contentKey, out ArraySegment<byte> contentKeySegment) ||
            !MemoryMarshal.TryGetArray(wrappingKey, out ArraySegment<byte> wrappingKeySegment))
        {
            return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }

        var packedBase64 = await CryptoInterop.EncryptAesGcmAsync(contentKeySegment, wrappingKeySegment);
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
        await CryptoInterop.EnsureInitializedAsync();

        if (!MemoryMarshal.TryGetArray(wrappingKey, out ArraySegment<byte> wrappingKeySegment))
        {
            return PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH);
        }

        try
        {
            // Bytes-out async via writable MemoryView output. Plaintext length
            // = (base64-decoded ciphertext length) − 16-byte AES-GCM tag.
            // SYSLIB1072 forces ArraySegment instead of Span on the Task path.
            var contentKeyLength = GetBase64DecodedLength(wrappedKey.Ciphertext) - 16;
            if (contentKeyLength <= 0)
            {
                return PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.DECRYPTION_FAILED);
            }
            var contentKey = new byte[contentKeyLength];
            await CryptoInterop.DecryptAesGcmIntoAsync(
                wrappedKey.Ciphertext, wrappedKey.Nonce,
                wrappingKeySegment, new ArraySegment<byte>(contentKey));

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
        await CryptoInterop.EnsureInitializedAsync();
        return CryptoInterop.IsSupported();
    }

    // ============================================================
    // BINARY UNPACKING HELPERS
    // ============================================================

    /// <summary>
    /// Decoded-byte length of a Base64-encoded ASCII string. Avoids allocating
    /// the intermediate byte[] just to learn its size — used to pre-allocate
    /// the writable MemoryView output for the bytes-out async bridge path.
    /// </summary>
    private static int GetBase64DecodedLength(string base64)
    {
        var len = (base64.Length / 4) * 3;
        if (base64.EndsWith("==", StringComparison.Ordinal))
        {
            return len - 2;
        }
        if (base64.EndsWith('='))
        {
            return len - 1;
        }
        return len;
    }

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
}
