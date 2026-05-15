using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.BouncyCastle;

/// <summary>
/// Cryptographic primitives over BouncyCastle — AES-256-GCM, ChaCha20-Poly1305,
/// Ed25519 signing/verifying. Consumed by <see cref="BouncyCastleCryptoProvider"/>
/// for the <see cref="Abstractions.ICryptoProvider"/> surface and directly by a
/// handful of cross-language vector tests.
///
/// <para>
/// Dead pre-P21 surface (string-keyed symmetric/asymmetric overloads,
/// CreateSignedMessage, VerifyBytes, SignBytes, Sign(string,string)) was
/// pruned in P21 phase 3 — see <c>docs/security/property-catalog.md</c>.
/// </para>
/// </summary>
public static class CryptoOperations
{
    private const int NonceLength = 12;
    private const int KeyLength = 32;

    // ============================================================
    // AES-256-GCM
    // ============================================================

    /// <summary>
    /// Encrypts data using AES-256-GCM with optional Associated Authenticated Data.
    /// </summary>
    public static byte[] EncryptAesGcm(byte[] plaintext, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        var keyBytes = key.ToArray();
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.GcmBlockCipher(new Org.BouncyCastle.Crypto.Engines.AesEngine());
            var parameters = new AeadParameters(
                new KeyParameter(keyBytes), 128, nonce, aad);
            cipher.Init(true, parameters);

            var output = new byte[cipher.GetOutputSize(plaintext.Length)];
            var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            cipher.DoFinal(output, len);

            return output;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Decrypts data using AES-256-GCM with optional Associated Authenticated Data.
    /// Returns null if authentication fails.
    /// </summary>
    public static byte[]? DecryptAesGcm(byte[] ciphertext, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        var keyBytes = key.ToArray();
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.GcmBlockCipher(new Org.BouncyCastle.Crypto.Engines.AesEngine());
            var parameters = new AeadParameters(
                new KeyParameter(keyBytes), 128, nonce, aad);
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
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    // ============================================================
    // CHACHA20-POLY1305
    // ============================================================

    /// <summary>
    /// Encrypts data using ChaCha20-Poly1305 with optional Associated Authenticated Data.
    /// Output is ciphertext || 16-byte Poly1305 tag (matches @awasm/noble envelope).
    /// </summary>
    public static byte[] EncryptChaCha20Poly1305(byte[] plaintext, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        var keyBytes = key.ToArray();
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            var parameters = new AeadParameters(
                new KeyParameter(keyBytes), 128, nonce, aad);
            cipher.Init(true, parameters);

            var output = new byte[cipher.GetOutputSize(plaintext.Length)];
            var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            cipher.DoFinal(output, len);

            return output;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Decrypts data using ChaCha20-Poly1305 with optional Associated Authenticated Data.
    /// Input is ciphertext || 16-byte Poly1305 tag.
    /// Returns null if authentication fails.
    /// </summary>
    public static byte[]? DecryptChaCha20Poly1305(byte[] ciphertextWithTag, ReadOnlySpan<byte> key, byte[] nonce, byte[]? aad = null)
    {
        var keyBytes = key.ToArray();
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            var parameters = new AeadParameters(
                new KeyParameter(keyBytes), 128, nonce, aad);
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
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    // ============================================================
    // ED25519 DIGITAL SIGNATURES
    // ============================================================

    /// <summary>
    /// Signs a UTF-8 message with an Ed25519 private key span and returns the
    /// Base64-encoded signature.
    /// </summary>
    public static PrfResult<string> Sign(string message, ReadOnlySpan<byte> privateKey)
    {
        try
        {
            if (privateKey.Length != KeyLength)
            {
                return PrfResult<string>.Fail(PrfErrorCode.INVALID_PRIVATE_KEY);
            }

            var messageBytes = Encoding.UTF8.GetBytes(message);
            var privateKeyBytes = privateKey.ToArray();
            var signer = new Ed25519Signer();
            try
            {
                var privateKeyParams = new Ed25519PrivateKeyParameters(privateKeyBytes, 0);
                signer.Init(true, privateKeyParams);
                signer.BlockUpdate(messageBytes, 0, messageBytes.Length);
                var signature = signer.GenerateSignature();
                return PrfResult<string>.Ok(Convert.ToBase64String(signature));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }
        catch
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }
    }

    /// <summary>
    /// Verifies a Base64-encoded Ed25519 signature against a UTF-8 message
    /// and Base64-encoded public key.
    /// </summary>
    public static bool Verify(string message, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            var signatureBytes = Convert.FromBase64String(signatureBase64);
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            if (signatureBytes.Length != 64 || publicKeyBytes.Length != KeyLength)
            {
                return false;
            }

            var messageBytes = Encoding.UTF8.GetBytes(message);
            var verifier = new Ed25519Signer();
            var publicKeyParams = new Ed25519PublicKeyParameters(publicKeyBytes, 0);
            verifier.Init(false, publicKeyParams);
            verifier.BlockUpdate(messageBytes, 0, messageBytes.Length);
            return verifier.VerifySignature(signatureBytes);
        }
        catch
        {
            return false;
        }
    }
}
