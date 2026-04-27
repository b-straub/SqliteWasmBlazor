using System.Runtime.Versioning;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Service for Ed25519 digital signatures using PRF-derived keys via ICryptoProvider.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class SigningService : ISigningService
{
    private readonly ISecureKeyCache _keyCache;
    private readonly IEd25519PublicKeyProvider _publicKeyProvider;
    private readonly ICryptoProvider _cryptoProvider;

    public SigningService(
        ISecureKeyCache keyCache,
        IEd25519PublicKeyProvider publicKeyProvider,
        ICryptoProvider cryptoProvider)
    {
        _keyCache = keyCache;
        _publicKeyProvider = publicKeyProvider;
        _cryptoProvider = cryptoProvider;
    }

       public async ValueTask<PrfResult<string>> SignAsync(string message, string keyIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(keyIdentifier);

        var cacheKey = GetCacheKey(keyIdentifier);
        var privateKey = _keyCache.TryGet(cacheKey);
        if (privateKey is null)
        {
            return PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        var result = await _cryptoProvider.SignAsync(message, privateKey);
        Array.Clear(privateKey, 0, privateKey.Length);
        return result;
    }

       public async ValueTask<bool> VerifyAsync(string message, string signature, string publicKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(signature);
        ArgumentException.ThrowIfNullOrEmpty(publicKey);

        return await _cryptoProvider.VerifyAsync(message, signature, publicKey);
    }

       public async ValueTask<PrfResult<SignedData>> CreateSignedMessageAsync(string message, string keyIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(keyIdentifier);

        var publicKey = _publicKeyProvider.GetEd25519PublicKey();
        if (publicKey is null)
        {
            return PrfResult<SignedData>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        var cacheKey = GetCacheKey(keyIdentifier);
        var privateKey = _keyCache.TryGet(cacheKey);
        if (privateKey is null)
        {
            return PrfResult<SignedData>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        // Create timestamped message (Unix timestamp in seconds)
        var timestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dataToSign = $"{timestampUnix}:{message}";

        var signResult = await _cryptoProvider.SignAsync(dataToSign, privateKey);
        Array.Clear(privateKey, 0, privateKey.Length);

        if (!signResult.Success || signResult.Value is null)
        {
            return PrfResult<SignedData>.Fail(signResult.ErrorCode ?? PrfErrorCode.SIGNING_FAILED);
        }

        var signedMessage = new SignedData(message, signResult.Value, publicKey, timestampUnix);

        return PrfResult<SignedData>.Ok(signedMessage);
    }

       public async ValueTask<bool> VerifySignedMessageAsync(SignedData signedData, int maxAgeSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(signedData);

        // Check timestamp age
        var messageTime = DateTimeOffset.FromUnixTimeSeconds(signedData.TimestampUnix);
        var age = DateTimeOffset.UtcNow - messageTime;

        if (age.TotalSeconds > maxAgeSeconds)
        {
            return false;
        }

        // Verify signature
        var dataToVerify = $"{signedData.TimestampUnix}:{signedData.Message}";
        return await _cryptoProvider.VerifyAsync(dataToVerify, signedData.Signature, signedData.PublicKey);
    }

    private static string GetCacheKey(string salt) => $"prf-ed25519-key:{salt}";
}

/// <summary>
/// Provider for Ed25519 public keys.
/// Implemented by PrfService to provide public keys for signing operations.
/// </summary>
public interface IEd25519PublicKeyProvider
{
    /// <summary>
    /// Get the current Ed25519 public key (Base64) or null if no session is active.
    /// </summary>
    string? GetEd25519PublicKey();
}
