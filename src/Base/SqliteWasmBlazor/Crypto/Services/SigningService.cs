using System.Runtime.Versioning;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Service for Ed25519 digital signatures using PRF-derived keys via ICryptoProvider.
/// Routes through the JS-side keyId cache so the Ed25519 private key never crosses
/// the C#↔JS boundary — JS holds it as a non-extractable <c>SubtleCrypto</c> CryptoKey.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class SigningService : ISigningService
{
    private readonly IEd25519PublicKeyProvider _publicKeyProvider;
    private readonly ICryptoProvider _cryptoProvider;

    public SigningService(
        IEd25519PublicKeyProvider publicKeyProvider,
        ICryptoProvider cryptoProvider)
    {
        _publicKeyProvider = publicKeyProvider;
        _cryptoProvider = cryptoProvider;
    }

       public ValueTask<PrfResult<string>> SignAsync(string message, string keyIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(keyIdentifier);

        return _cryptoProvider.SignWithKeyIdAsync(message, PrfKeyConventions.GetJsKeyId(keyIdentifier));
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

        // Create timestamped message (Unix timestamp in seconds)
        var timestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dataToSign = $"{timestampUnix}:{message}";

        var signResult = await _cryptoProvider.SignWithKeyIdAsync(
            dataToSign, PrfKeyConventions.GetJsKeyId(keyIdentifier));

        if (!signResult.Success || signResult.Value is null)
        {
            return PrfResult<SignedData>.Fail(signResult.ErrorCode ?? PrfErrorCode.SIGNING_FAILED);
        }

        return PrfResult<SignedData>.Ok(new SignedData(message, signResult.Value, publicKey, timestampUnix));
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
