using System.Runtime.Versioning;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Service for asymmetric (ECIES) encryption using PRF-derived keys via ICryptoProvider.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class AsymmetricEncryptionService : IAsymmetricEncryption
{
    private readonly ISecureKeyCache _keyCache;
    private readonly ICryptoProvider _cryptoProvider;

    public AsymmetricEncryptionService(
        ISecureKeyCache keyCache,
        ICryptoProvider cryptoProvider)
    {
        _keyCache = keyCache;
        _cryptoProvider = cryptoProvider;
    }

       public async ValueTask<PrfResult<AsymmetricEncryptedData>> EncryptAsync(string message, string recipientPublicKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(recipientPublicKey);

        return await _cryptoProvider.EncryptAsymmetricAsync(message, recipientPublicKey);
    }

       public async ValueTask<PrfResult<AsymmetricEncryptedData>> SignAndEncryptAsync(
        string message,
        string recipientPublicKey,
        ISigningService signingService,
        string senderEd25519PublicKey,
        string keyIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(recipientPublicKey);
        ArgumentException.ThrowIfNullOrEmpty(senderEd25519PublicKey);

        // Sign the plaintext
        var signResult = await signingService.SignAsync(message, keyIdentifier);
        if (!signResult.Success || signResult.Value is null)
        {
            return PrfResult<AsymmetricEncryptedData>.Fail(signResult.ErrorCode ?? PrfErrorCode.SIGNING_FAILED);
        }

        // Bundle into signed envelope — this entire envelope gets encrypted
        var envelope = new SignedEnvelope(message, signResult.Value, senderEd25519PublicKey);
        var envelopeJson = System.Text.Json.JsonSerializer.Serialize(envelope,
            Abstractions.Json.SharedJsonContext.Default.SignedEnvelope);

        // Encrypt the envelope (not the raw plaintext)
        return await _cryptoProvider.EncryptAsymmetricAsync(envelopeJson, recipientPublicKey);
    }

       public async ValueTask<PrfResult<string>> DecryptAsync(AsymmetricEncryptedData asymmetricEncrypted, string salt)
    {
        ArgumentNullException.ThrowIfNull(asymmetricEncrypted);
        ArgumentException.ThrowIfNullOrEmpty(salt);

        var cacheKey = GetCacheKey(salt);
        var privateKey = _keyCache.TryGet(cacheKey);
        if (privateKey is null)
        {
            return PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        var result = await _cryptoProvider.DecryptAsymmetricAsync(asymmetricEncrypted, privateKey);
        Array.Clear(privateKey, 0, privateKey.Length);
        return result;
    }

       public async ValueTask<PrfResult<DecryptedData>> DecryptAndVerifyAsync(
        AsymmetricEncryptedData asymmetricEncrypted,
        string keyIdentifier,
        ISigningService signingService)
    {
        // Decrypt
        var decryptResult = await DecryptAsync(asymmetricEncrypted, keyIdentifier);
        if (!decryptResult.Success || decryptResult.Value is null)
        {
            return PrfResult<DecryptedData>.Fail(decryptResult.ErrorCode ?? PrfErrorCode.DECRYPTION_FAILED);
        }

        SignedEnvelope? envelope;
        try
        {
            envelope = System.Text.Json.JsonSerializer.Deserialize(decryptResult.Value,
                Abstractions.Json.SharedJsonContext.Default.SignedEnvelope);
        }
        catch (System.Text.Json.JsonException)
        {
            return PrfResult<DecryptedData>.Fail(PrfErrorCode.INCOMPATIBLE_FORMAT);
        }

        if (envelope is null)
        {
            return PrfResult<DecryptedData>.Fail(PrfErrorCode.INCOMPATIBLE_FORMAT);
        }

        var signatureValid = await signingService.VerifyAsync(
            envelope.Message,
            envelope.Signature,
            envelope.SenderEd25519PublicKey);

        return PrfResult<DecryptedData>.Ok(new DecryptedData(
            envelope.Message,
            envelope.SenderEd25519PublicKey,
            signatureValid));
    }

    private static string GetCacheKey(string salt) => $"prf-key:{salt}";
}
