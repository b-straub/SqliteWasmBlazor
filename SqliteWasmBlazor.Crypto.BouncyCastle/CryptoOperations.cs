using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using SqliteWasmBlazor.Crypto.Abstractions.Extensions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.BouncyCastle;

/// <summary>
/// Cryptographic operations using BouncyCastle.
/// Provides X25519 + AES-256-GCM encryption and Ed25519 signing.
/// Compatible with WASM and server-side execution.
/// </summary>
public static class CryptoOperations
{
    private const int NonceLength = 12;
    private const int KeyLength = 32;
    // HKDF info must match Noble.js for interoperability
    private static readonly byte[] HkdfInfo = "ecies-aes-gcm"u8.ToArray();

    /// <summary>
    /// Encrypts a message using AES-256-GCM symmetric encryption.
    /// </summary>
    public static PrfResult<SymmetricEncryptedData> EncryptSymmetric(string plaintext, string keyBase64)
    {
        var keyBytes = Convert.FromBase64String(keyBase64);
        try
        {
            return EncryptSymmetric(plaintext, keyBytes);
        }
        finally
        {
            Array.Clear(keyBytes, 0, keyBytes.Length);
        }
    }

    /// <summary>
    /// Encrypts a message using AES-256-GCM symmetric encryption.
    /// Preferred overload - avoids Base64 conversion and works directly with key bytes.
    /// </summary>
    public static PrfResult<SymmetricEncryptedData> EncryptSymmetric(
        string plaintext,
        ReadOnlySpan<byte> key,
        string? associatedData = null)
    {
        try
        {
            if (key.Length != KeyLength)
            {
                return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.INVALID_DATA);
            }

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceLength];
            new SecureRandom().NextBytes(nonce);

            var aadBytes = associatedData is not null ? Encoding.UTF8.GetBytes(associatedData) : null;
            var ciphertext = EncryptAesGcm(plaintextBytes, key, nonce, aadBytes);

            return PrfResult<SymmetricEncryptedData>.Ok(new SymmetricEncryptedData(
                Ciphertext: Convert.ToBase64String(ciphertext),
                Nonce: Convert.ToBase64String(nonce)
            ));
        }
        catch
        {
            return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }
    }

    /// <summary>
    /// Decrypts a message using AES-256-GCM symmetric encryption.
    /// </summary>
    public static PrfResult<string> DecryptSymmetric(SymmetricEncryptedData encrypted, string keyBase64)
    {
        var keyBytes = Convert.FromBase64String(keyBase64);
        try
        {
            return DecryptSymmetric(encrypted, keyBytes);
        }
        finally
        {
            Array.Clear(keyBytes, 0, keyBytes.Length);
        }
    }

    /// <summary>
    /// Decrypts a message using AES-256-GCM symmetric encryption.
    /// Preferred overload - avoids Base64 conversion and works directly with key bytes.
    /// </summary>
    public static PrfResult<string> DecryptSymmetric(SymmetricEncryptedData encrypted, ReadOnlySpan<byte> key, string? associatedData = null)
    {
        try
        {
            if (key.Length != KeyLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA);
            }

            var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            var nonce = Convert.FromBase64String(encrypted.Nonce);

            if (nonce.Length != NonceLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA);
            }

            var aadBytes = associatedData is not null ? Encoding.UTF8.GetBytes(associatedData) : null;
            var plaintext = DecryptAesGcm(ciphertext, key, nonce, aadBytes);

            if (plaintext is null)
            {
                return PrfResult<string>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH);
            }

            return PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    /// <summary>
    /// Encrypts a message using ECIES (X25519 + AES-256-GCM).
    /// </summary>
    public static PrfResult<AsymmetricEncryptedData> EncryptAsymmetric(string plaintext, string recipientPublicKeyBase64)
    {
        try
        {
            var recipientPublicKeyBytes = Convert.FromBase64String(recipientPublicKeyBase64);
            if (recipientPublicKeyBytes.Length != KeyLength)
            {
                return PrfResult<AsymmetricEncryptedData>.Fail(PrfErrorCode.INVALID_PUBLIC_KEY);
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

            // Derive encryption key using HKDF
            var encryptionKey = DeriveEncryptionKey(sharedSecret);

            // Encrypt with AES-256-GCM
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceLength];
            new SecureRandom().NextBytes(nonce);

            var ciphertext = EncryptAesGcm(plaintextBytes, encryptionKey, nonce);

            // Clear sensitive data
            Array.Clear(sharedSecret, 0, sharedSecret.Length);
            Array.Clear(encryptionKey, 0, encryptionKey.Length);

            return PrfResult<AsymmetricEncryptedData>.Ok(new AsymmetricEncryptedData(
                EphemeralPublicKey: Convert.ToBase64String(ephemeralPublicKeyBytes),
                Ciphertext: Convert.ToBase64String(ciphertext),
                Nonce: Convert.ToBase64String(nonce)
            ));
        }
        catch
        {
            return PrfResult<AsymmetricEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
        }
    }

    /// <summary>
    /// Decrypts a message using ECIES (X25519 + AES-256-GCM).
    /// </summary>
    public static PrfResult<string> DecryptAsymmetric(AsymmetricEncryptedData asymmetricEncrypted, string privateKeyBase64)
    {
        var privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
        try
        {
            return DecryptAsymmetric(asymmetricEncrypted, privateKeyBytes);
        }
        finally
        {
            Array.Clear(privateKeyBytes, 0, privateKeyBytes.Length);
        }
    }

    /// <summary>
    /// Decrypts a message using ECIES (X25519 + AES-256-GCM).
    /// Preferred overload - avoids Base64 conversion and works directly with key bytes.
    /// </summary>
    public static PrfResult<string> DecryptAsymmetric(AsymmetricEncryptedData asymmetricEncrypted, ReadOnlySpan<byte> privateKey)
    {
        try
        {
            if (privateKey.Length != KeyLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_PRIVATE_KEY);
            }

            var ephemeralPublicKeyBytes = Convert.FromBase64String(asymmetricEncrypted.EphemeralPublicKey);
            if (ephemeralPublicKeyBytes.Length != KeyLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA);
            }

            var ciphertext = Convert.FromBase64String(asymmetricEncrypted.Ciphertext);
            var nonce = Convert.FromBase64String(asymmetricEncrypted.Nonce);

            if (nonce.Length != NonceLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_DATA);
            }

            var privateKeyParam = new X25519PrivateKeyParameters(privateKey.ToArray(), 0);
            var ephemeralPublicKey = new X25519PublicKeyParameters(ephemeralPublicKeyBytes, 0);

            // Perform X25519 key agreement
            var agreement = new X25519Agreement();
            agreement.Init(privateKeyParam);
            var sharedSecret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(ephemeralPublicKey, sharedSecret, 0);

            // Derive encryption key using HKDF
            var encryptionKey = DeriveEncryptionKey(sharedSecret);

            // Decrypt with AES-256-GCM
            var plaintext = DecryptAesGcm(ciphertext, encryptionKey, nonce);

            // Clear sensitive data
            Array.Clear(sharedSecret, 0, sharedSecret.Length);
            Array.Clear(encryptionKey, 0, encryptionKey.Length);

            if (plaintext is null)
            {
                return PrfResult<string>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH);
            }

            return PrfResult<string>.Ok(Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }
    }

    /// <summary>
    /// Derives an encryption key from the shared secret using HKDF-SHA256.
    /// Uses null salt (= 32 zeros) to match Noble.js implementation.
    /// Uses BouncyCastle for WASM compatibility (System.Security.Cryptography.HKDF not supported in WASM).
    /// </summary>
    private static byte[] DeriveEncryptionKey(byte[] sharedSecret)
    {
        return HkdfDeriveKey(sharedSecret, null, HkdfInfo, KeyLength);
    }

    /// <summary>
    /// HKDF key derivation using BouncyCastle (WASM-compatible).
    /// Per RFC 5869: if salt is null, uses HashLen zeros (32 bytes for SHA-256) to match Noble.js.
    /// </summary>
    private static byte[] HkdfDeriveKey(byte[] ikm, byte[]? salt, byte[]? info, int outputLength)
    {
        var hkdf = new HkdfBytesGenerator(new Sha256Digest());
        var effectiveSalt = salt ?? new byte[32];
        var hkdfParams = new HkdfParameters(ikm, effectiveSalt, info);
        hkdf.Init(hkdfParams);

        var output = new byte[outputLength];
        hkdf.GenerateBytes(output, 0, outputLength);
        return output;
    }

    // ============================================================
    // AES-256-GCM ENCRYPTION
    // ============================================================

    /// <summary>
    /// Encrypts data using AES-256-GCM with optional Associated Authenticated Data.
    /// </summary>
    public static byte[] EncryptAesGcm(byte[] plaintext, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.GcmBlockCipher(new Org.BouncyCastle.Crypto.Engines.AesEngine());
        var parameters = new AeadParameters(
            new KeyParameter(key.ToArray()), 128, nonce, aad);
        cipher.Init(true, parameters);

        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, len);

        return output;
    }

    /// <summary>
    /// Decrypts data using AES-256-GCM with optional Associated Authenticated Data.
    /// Returns null if authentication fails.
    /// </summary>
    public static byte[]? DecryptAesGcm(byte[] ciphertext, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.GcmBlockCipher(new Org.BouncyCastle.Crypto.Engines.AesEngine());
            var parameters = new AeadParameters(
                new KeyParameter(key.ToArray()), 128, nonce, aad);
            cipher.Init(false, parameters);

            var output = new byte[cipher.GetOutputSize(ciphertext.Length)];
            var len = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, output, 0);
            cipher.DoFinal(output, len);

            return output;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return null;
        }
    }

    // ============================================================
    // CHACHA20-POLY1305 ENCRYPTION
    // ============================================================

    /// <summary>
    /// Encrypts data using ChaCha20-Poly1305 with optional Associated Authenticated Data.
    /// Output is ciphertext || 16-byte Poly1305 tag (matches @awasm/noble envelope).
    /// </summary>
    public static byte[] EncryptChaCha20Poly1305(byte[] plaintext, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        var parameters = new AeadParameters(
            new KeyParameter(key.ToArray()), 128, nonce, aad);
        cipher.Init(true, parameters);

        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, len);

        return output;
    }

    /// <summary>
    /// Decrypts data using ChaCha20-Poly1305 with optional Associated Authenticated Data.
    /// Input is ciphertext || 16-byte Poly1305 tag.
    /// Returns null if authentication fails.
    /// </summary>
    public static byte[]? DecryptChaCha20Poly1305(byte[] ciphertextWithTag, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            var parameters = new AeadParameters(
                new KeyParameter(key.ToArray()), 128, nonce, aad);
            cipher.Init(false, parameters);

            var output = new byte[cipher.GetOutputSize(ciphertextWithTag.Length)];
            var len = cipher.ProcessBytes(ciphertextWithTag, 0, ciphertextWithTag.Length, output, 0);
            cipher.DoFinal(output, len);

            return output;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return null;
        }
    }

    // ============================================================
    // ED25519 DIGITAL SIGNATURES
    // ============================================================

    /// <summary>
    /// Signs a message with an Ed25519 private key.
    /// </summary>
    /// <param name="message">The message to sign (UTF-8 string)</param>
    /// <param name="privateKeyBase64">Base64-encoded Ed25519 private key (32-byte seed)</param>
    /// <returns>Result containing Base64-encoded signature (64 bytes)</returns>
    public static PrfResult<string> Sign(string message, string privateKeyBase64)
    {
        try
        {
            var privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
            if (privateKeyBytes.Length != KeyLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_PRIVATE_KEY);
            }

            var messageBytes = Encoding.UTF8.GetBytes(message);
            var signature = SignBytes(messageBytes, privateKeyBytes);

            return PrfResult<string>.Ok(Convert.ToBase64String(signature));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }
    }

    /// <summary>
    /// Signs a message with an Ed25519 private key from a span.
    /// Preferred overload for secure key cache - avoids creating managed copies.
    /// </summary>
    /// <param name="message">The message to sign (UTF-8 string)</param>
    /// <param name="privateKey">Ed25519 private key (32-byte seed)</param>
    /// <returns>Result containing Base64-encoded signature (64 bytes)</returns>
    public static PrfResult<string> Sign(string message, ReadOnlySpan<byte> privateKey)
    {
        try
        {
            if (privateKey.Length != KeyLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_PRIVATE_KEY);
            }

            var messageBytes = Encoding.UTF8.GetBytes(message);
            var signature = SignBytes(messageBytes, privateKey.ToArray());

            return PrfResult<string>.Ok(Convert.ToBase64String(signature));
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }
    }

    /// <summary>
    /// Signs raw bytes with an Ed25519 private key.
    /// </summary>
    /// <param name="message">The message bytes to sign</param>
    /// <param name="privateKey">Ed25519 private key (32-byte seed)</param>
    /// <returns>Ed25519 signature (64 bytes)</returns>
    public static byte[] SignBytes(byte[] message, byte[] privateKey)
    {
        if (privateKey.Length != KeyLength)
        {
            throw new ArgumentException("Ed25519 private key must be 32 bytes", nameof(privateKey));
        }

        var signer = new Ed25519Signer();
        var privateKeyParams = new Ed25519PrivateKeyParameters(privateKey, 0);

        signer.Init(true, privateKeyParams);
        signer.BlockUpdate(message, 0, message.Length);

        return signer.GenerateSignature();
    }

    /// <summary>
    /// Verifies an Ed25519 signature.
    /// </summary>
    /// <param name="message">The original message (UTF-8 string)</param>
    /// <param name="signatureBase64">Base64-encoded signature (64 bytes)</param>
    /// <param name="publicKeyBase64">Base64-encoded Ed25519 public key (32 bytes)</param>
    /// <returns>True if signature is valid, false otherwise</returns>
    public static bool Verify(string message, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            var signatureBytes = Convert.FromBase64String(signatureBase64);
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            var messageBytes = Encoding.UTF8.GetBytes(message);

            return VerifyBytes(messageBytes, signatureBytes, publicKeyBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies an Ed25519 signature on raw bytes.
    /// </summary>
    /// <param name="message">The message bytes</param>
    /// <param name="signature">Ed25519 signature (64 bytes)</param>
    /// <param name="publicKey">Ed25519 public key (32 bytes)</param>
    /// <returns>True if signature is valid, false otherwise</returns>
    public static bool VerifyBytes(byte[] message, byte[] signature, byte[] publicKey)
    {
        if (signature.Length != 64)
        {
            return false;
        }

        if (publicKey.Length != KeyLength)
        {
            return false;
        }

        try
        {
            var verifier = new Ed25519Signer();
            var publicKeyParams = new Ed25519PublicKeyParameters(publicKey, 0);

            verifier.Init(false, publicKeyParams);
            verifier.BlockUpdate(message, 0, message.Length);

            return verifier.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a signed message with timestamp.
    /// </summary>
    /// <param name="message">The message to sign</param>
    /// <param name="privateKeyBase64">Base64-encoded Ed25519 private key</param>
    /// <param name="publicKeyBase64">Base64-encoded Ed25519 public key (for inclusion in result)</param>
    /// <returns>SignedMessage containing message, signature, public key, and timestamp</returns>
    public static PrfResult<SignedData> CreateSignedMessage(
        string message,
        string privateKeyBase64,
        string publicKeyBase64)
    {
        var timestampUnix = DateTimeExtensions.GetUnixSecondsNow();
        var messageWithTimestamp = $"{message}|{timestampUnix}";

        var signResult = Sign(messageWithTimestamp, privateKeyBase64);
        if (!signResult.Success || signResult.Value is null)
        {
            return PrfResult<SignedData>.Fail(signResult.ErrorCode ?? PrfErrorCode.SIGNING_FAILED);
        }

        return PrfResult<SignedData>.Ok(new SignedData(
            Message: message,
            Signature: signResult.Value,
            PublicKey: publicKeyBase64,
            TimestampUnix: timestampUnix
        ));
    }

    /// <summary>
    /// Creates a signed message with timestamp using a key span.
    /// Preferred overload for secure key cache - avoids creating managed copies.
    /// </summary>
    /// <param name="message">The message to sign</param>
    /// <param name="privateKey">Ed25519 private key (32-byte seed)</param>
    /// <param name="publicKeyBase64">Base64-encoded Ed25519 public key (for inclusion in result)</param>
    /// <returns>SignedMessage containing message, signature, public key, and timestamp</returns>
    public static PrfResult<SignedData> CreateSignedMessage(
        string message,
        ReadOnlySpan<byte> privateKey,
        string publicKeyBase64)
    {
        var timestampUnix = DateTimeExtensions.GetUnixSecondsNow();
        var messageWithTimestamp = $"{message}|{timestampUnix}";

        var signResult = Sign(messageWithTimestamp, privateKey);
        if (!signResult.Success || signResult.Value is null)
        {
            return PrfResult<SignedData>.Fail(signResult.ErrorCode ?? PrfErrorCode.SIGNING_FAILED);
        }

        return PrfResult<SignedData>.Ok(new SignedData(
            Message: message,
            Signature: signResult.Value,
            PublicKey: publicKeyBase64,
            TimestampUnix: timestampUnix
        ));
    }

    /// <summary>
    /// Verifies a signed message, including timestamp validation.
    /// </summary>
    /// <param name="signedData">The signed message to verify</param>
    /// <param name="maxAgeSeconds">Maximum age of the signature in seconds (default 5 minutes)</param>
    /// <returns>True if signature is valid and not expired</returns>
    public static bool VerifySignedMessage(SignedData signedData, int maxAgeSeconds = 300)
    {
        // Check timestamp is not too old
        var nowUnix = DateTimeExtensions.GetUnixSecondsNow();
        var ageSeconds = nowUnix - signedData.TimestampUnix;
        if (ageSeconds > maxAgeSeconds || ageSeconds < -60) // Allow 60s clock skew
        {
            return false;
        }

        // Reconstruct the signed message
        var messageWithTimestamp = $"{signedData.Message}|{signedData.TimestampUnix}";

        return Verify(messageWithTimestamp, signedData.Signature, signedData.PublicKey);
    }
}
