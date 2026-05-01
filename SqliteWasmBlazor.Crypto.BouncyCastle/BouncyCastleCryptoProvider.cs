using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.BouncyCastle;

/// <summary>
/// BouncyCastle-based crypto provider using AES-256-GCM.
/// </summary>
public sealed class BouncyCastleCryptoProvider : ICryptoProvider
{
    private const int NonceLength = 12;
    private const int KeyLength = 32;

    // HKDF info string must match Noble.js for interoperability
    private static readonly byte[] HkdfInfoAesGcm = "ecies-aes-gcm"u8.ToArray();

    public string ProviderName => "BouncyCastle";

    // ============================================================
    // SYMMETRIC ENCRYPTION (AES-256-GCM)
    // ============================================================

    public ValueTask<PrfResult<SymmetricEncryptedData>> EncryptSymmetricAsync(
        string plaintext,
        ReadOnlyMemory<byte> key,
        string? associatedData = null)
    {
        try
        {
            if (key.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceLength];
            new SecureRandom().NextBytes(nonce);

            var aadBytes = associatedData is not null ? Encoding.UTF8.GetBytes(associatedData) : null;
            var ciphertext = CryptoOperations.EncryptAesGcm(plaintextBytes, key.Span, nonce, aadBytes);

            return ValueTask.FromResult(PrfResult<SymmetricEncryptedData>.Ok(new SymmetricEncryptedData(
                Ciphertext: Convert.ToBase64String(ciphertext),
                Nonce: Convert.ToBase64String(nonce)
            )));
        }
        catch
        {
            return ValueTask.FromResult(PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED));
        }
    }

    public ValueTask<PrfResult<string>> DecryptSymmetricAsync(
        SymmetricEncryptedData encrypted,
        ReadOnlyMemory<byte> key,
        string? associatedData = null)
    {
        try
        {
            if (key.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            var nonce = Convert.FromBase64String(encrypted.Nonce);

            if (nonce.Length != NonceLength)
            {
                return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var aadBytes = associatedData is not null ? Encoding.UTF8.GetBytes(associatedData) : null;
            var plaintext = CryptoOperations.DecryptAesGcm(ciphertext, key.Span, nonce, aadBytes);

            if (plaintext is null)
            {
                return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH));
            }

            return ValueTask.FromResult(PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext)));
        }
        catch
        {
            return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED));
        }
    }

    // ============================================================
    // ASYMMETRIC ENCRYPTION (ECIES: X25519 + AES-256-GCM)
    // ============================================================

    public ValueTask<PrfResult<AsymmetricEncryptedData>> EncryptAsymmetricAsync(
        string plaintext,
        string recipientPublicKeyBase64)
    {
        try
        {
            var recipientPublicKeyBytes = Convert.FromBase64String(recipientPublicKeyBase64);
            if (recipientPublicKeyBytes.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<AsymmetricEncryptedData>.Fail(PrfErrorCode.INVALID_PUBLIC_KEY));
            }

            var recipientPublicKey = new X25519PublicKeyParameters(recipientPublicKeyBytes, 0);

            // Generate ephemeral key pair
            var random = new SecureRandom();
            var generator = new X25519KeyPairGenerator();
            generator.Init(new X25519KeyGenerationParameters(random));
            var ephemeralKeyPair = generator.GenerateKeyPair();

            var ephemeralPrivateKey = (X25519PrivateKeyParameters)ephemeralKeyPair.Private;
            var ephemeralPublicKey = (X25519PublicKeyParameters)ephemeralKeyPair.Public;

            // Perform X25519 key agreement
            var agreement = new X25519Agreement();
            agreement.Init(ephemeralPrivateKey);
            var sharedSecret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(recipientPublicKey, sharedSecret, 0);

            // Get ephemeral public key bytes
            var ephemeralPublicKeyBytes = new byte[32];
            ephemeralPublicKey.Encode(ephemeralPublicKeyBytes, 0);

            // Derive encryption key using HKDF (null salt = 32 zeros to match Noble.js)
            var encryptionKey = KeyGenerator.HkdfDeriveKey(sharedSecret, null, HkdfInfoAesGcm, KeyLength);

            // Encrypt with AES-256-GCM
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceLength];
            random.NextBytes(nonce);

            var ciphertext = CryptoOperations.EncryptAesGcm(plaintextBytes, encryptionKey, nonce);

            // Clear sensitive data
            Array.Clear(sharedSecret, 0, sharedSecret.Length);
            Array.Clear(encryptionKey, 0, encryptionKey.Length);

            return ValueTask.FromResult(PrfResult<AsymmetricEncryptedData>.Ok(new AsymmetricEncryptedData(
                EphemeralPublicKey: Convert.ToBase64String(ephemeralPublicKeyBytes),
                Ciphertext: Convert.ToBase64String(ciphertext),
                Nonce: Convert.ToBase64String(nonce)
            )));
        }
        catch
        {
            return ValueTask.FromResult(PrfResult<AsymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED));
        }
    }

    public ValueTask<PrfResult<string>> DecryptAsymmetricAsync(
        AsymmetricEncryptedData asymmetricEncrypted,
        ReadOnlyMemory<byte> privateKey)
    {
        try
        {
            if (privateKey.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.INVALID_PRIVATE_KEY));
            }

            var ephemeralPublicKeyBytes = Convert.FromBase64String(asymmetricEncrypted.EphemeralPublicKey);
            if (ephemeralPublicKeyBytes.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var ciphertext = Convert.FromBase64String(asymmetricEncrypted.Ciphertext);
            var nonce = Convert.FromBase64String(asymmetricEncrypted.Nonce);

            if (nonce.Length != NonceLength)
            {
                return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var privateKeyParam = new X25519PrivateKeyParameters(privateKey.ToArray(), 0);
            var ephemeralPublicKey = new X25519PublicKeyParameters(ephemeralPublicKeyBytes, 0);

            // Perform X25519 key agreement
            var agreement = new X25519Agreement();
            agreement.Init(privateKeyParam);
            var sharedSecret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(ephemeralPublicKey, sharedSecret, 0);

            // Derive encryption key using HKDF (null salt = 32 zeros to match Noble.js)
            var encryptionKey = KeyGenerator.HkdfDeriveKey(sharedSecret, null, HkdfInfoAesGcm, KeyLength);

            // Decrypt with AES-256-GCM
            var plaintext = CryptoOperations.DecryptAesGcm(ciphertext, encryptionKey, nonce);

            // Clear sensitive data
            Array.Clear(sharedSecret, 0, sharedSecret.Length);
            Array.Clear(encryptionKey, 0, encryptionKey.Length);

            if (plaintext is null)
            {
                return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH));
            }

            return ValueTask.FromResult(PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext)));
        }
        catch
        {
            return ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED));
        }
    }

    // ============================================================
    // ED25519 DIGITAL SIGNATURES
    // ============================================================

    public ValueTask<PrfResult<string>> SignAsync(string message, ReadOnlyMemory<byte> privateKey)
    {
        var result = CryptoOperations.Sign(message, privateKey.Span);
        return ValueTask.FromResult(result);
    }

    public ValueTask<bool> VerifyAsync(string message, string signatureBase64, string publicKeyBase64)
    {
        var result = CryptoOperations.Verify(message, signatureBase64, publicKeyBase64);
        return ValueTask.FromResult(result);
    }

    // ============================================================
    // KEY GENERATION & DERIVATION
    // ============================================================

    public ValueTask<DualKeyPairFull> DeriveDualKeyPairAsync(ReadOnlyMemory<byte> prfSeed)
    {
        var dualKeyPair = KeyGenerator.DeriveDualKeyPair(prfSeed.ToArray());
        return ValueTask.FromResult(dualKeyPair);
    }

    public ValueTask<string> GenerateSaltAsync(int length = 32)
    {
        var salt = KeyGenerator.GenerateSalt(length);
        return ValueTask.FromResult(salt);
    }

    public ValueTask<PrfResult<ReadOnlyMemory<byte>>> DeriveWrappingKeyAsync(
        ReadOnlyMemory<byte> ownPrivateKey,
        string recipientPublicKeyBase64,
        string context)
    {
        try
        {
            if (ownPrivateKey.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.INVALID_PRIVATE_KEY));
            }

            var recipientPublicKeyBytes = Convert.FromBase64String(recipientPublicKeyBase64);
            if (recipientPublicKeyBytes.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.INVALID_PUBLIC_KEY));
            }

            // X25519 ECDH
            var privateKeyParam = new X25519PrivateKeyParameters(ownPrivateKey.ToArray(), 0);
            var publicKeyParam = new X25519PublicKeyParameters(recipientPublicKeyBytes, 0);

            var agreement = new X25519Agreement();
            agreement.Init(privateKeyParam);
            var sharedSecret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(publicKeyParam, sharedSecret, 0);

            // HKDF with context as info
            var contextBytes = Encoding.UTF8.GetBytes(context);
            var wrappingKey = KeyGenerator.HkdfDeriveKey(sharedSecret, null, contextBytes, KeyLength);

            // Clear shared secret
            Array.Clear(sharedSecret, 0, sharedSecret.Length);

            return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Ok(wrappingKey));
        }
        catch
        {
            return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED));
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> GenerateContentKeyAsync()
    {
        var key = new byte[KeyLength];
        new SecureRandom().NextBytes(key);
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(key);
    }

    public ValueTask<PrfResult<SymmetricEncryptedData>> WrapContentKeyAsync(
        ReadOnlyMemory<byte> contentKey,
        ReadOnlyMemory<byte> wrappingKey)
    {
        try
        {
            if (contentKey.Length != KeyLength || wrappingKey.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var nonce = new byte[NonceLength];
            new SecureRandom().NextBytes(nonce);

            var ciphertext = CryptoOperations.EncryptAesGcm(contentKey.ToArray(), wrappingKey.Span, nonce);

            return ValueTask.FromResult(PrfResult<SymmetricEncryptedData>.Ok(new SymmetricEncryptedData(
                Ciphertext: Convert.ToBase64String(ciphertext),
                Nonce: Convert.ToBase64String(nonce)
            )));
        }
        catch
        {
            return ValueTask.FromResult(PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED));
        }
    }

    public ValueTask<PrfResult<ReadOnlyMemory<byte>>> UnwrapContentKeyAsync(
        SymmetricEncryptedData wrappedKey,
        ReadOnlyMemory<byte> wrappingKey)
    {
        try
        {
            if (wrappingKey.Length != KeyLength)
            {
                return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var ciphertext = Convert.FromBase64String(wrappedKey.Ciphertext);
            var nonce = Convert.FromBase64String(wrappedKey.Nonce);

            if (nonce.Length != NonceLength)
            {
                return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.INVALID_DATA));
            }

            var contentKey = CryptoOperations.DecryptAesGcm(ciphertext, wrappingKey.Span, nonce);

            if (contentKey is null)
            {
                return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH));
            }

            return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Ok(new ReadOnlyMemory<byte>(contentKey)));
        }
        catch
        {
            return ValueTask.FromResult(PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.DECRYPTION_FAILED));
        }
    }
}
