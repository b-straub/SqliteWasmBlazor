using System.Runtime.Versioning;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Service for symmetric encryption using pre-derived keys from <see cref="ISecureKeyCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// Callers pass a fully-qualified cache key (e.g. the <c>prf-domain:{domainId}</c> handle
/// returned by <see cref="IPrfService.DeriveDomainKeyAsync"/>). The service looks up the
/// pre-derived 32-byte key in the secure cache and hands it to <see cref="ICryptoProvider"/>
/// for the AEAD operation. No HKDF happens here — domain separation is the caller's
/// responsibility, performed once via <see cref="IPrfService.DeriveDomainKeyAsync"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class SymmetricEncryptionService : ISymmetricEncryption
{
    private readonly ISecureKeyCache _keyCache;
    private readonly ICryptoProvider _cryptoProvider;

    public SymmetricEncryptionService(ISecureKeyCache keyCache, ICryptoProvider cryptoProvider)
    {
        _keyCache = keyCache;
        _cryptoProvider = cryptoProvider;
    }

       public async ValueTask<PrfResult<SymmetricEncryptedData>> EncryptAsync(string message, string keyIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(keyIdentifier);

        var key = _keyCache.TryGet(keyIdentifier);
        if (key is null)
        {
            return PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        try
        {
            return await _cryptoProvider.EncryptSymmetricAsync(message, key);
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }

       public async ValueTask<PrfResult<string>> DecryptAsync(SymmetricEncryptedData encrypted, string keyIdentifier)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentException.ThrowIfNullOrEmpty(keyIdentifier);

        var key = _keyCache.TryGet(keyIdentifier);
        if (key is null)
        {
            return PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        try
        {
            return await _cryptoProvider.DecryptSymmetricAsync(encrypted, key);
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }
}
