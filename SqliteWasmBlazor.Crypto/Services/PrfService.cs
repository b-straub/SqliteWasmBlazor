using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using R3;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Interop;
using SqliteWasmBlazor.Crypto.Json;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Implementation of PRF service using JSImport for WebAuthn operations
/// and ICryptoProvider for key derivation (Noble.js implementation).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class PrfService : IPrfService, IEd25519PublicKeyProvider, IAsyncDisposable
{
    private readonly PrfOptions _options;
    private readonly KeyCacheOptions _cacheOptions;
    private readonly SqliteWasmBlazorCryptoOptions _sqliteWasmBlazorCryptoOptions;
    private readonly ISecureKeyCache _keyCache;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    // Public-key cache (not sensitive). One salt per app ⇒ at most one entry each.
    private string? _cachedX25519PublicKey;
    private string? _cachedEd25519PublicKey;

       public KeyCacheStrategy CacheStrategy => _cacheOptions.Strategy;

       public string Salt => _options.Salt;

       public Observable<string> KeyExpired => _keyCache.KeyExpired;

    public PrfService(
        IOptions<PrfOptions> options,
        IOptions<KeyCacheOptions> cacheOptions,
        IOptions<SqliteWasmBlazorCryptoOptions> cryptoOptions,
        ISecureKeyCache keyCache,
        ICryptoProvider cryptoProvider)
    {
        _options = options.Value;
        _cacheOptions = cacheOptions.Value;
        _sqliteWasmBlazorCryptoOptions = cryptoOptions.Value;
        _keyCache = keyCache;
        _cryptoProvider = cryptoProvider;

        // Configure-once for the static interop. Idempotent — see NobleInterop.Configure.
        NobleInterop.Configure(_sqliteWasmBlazorCryptoOptions.BaseHref, _sqliteWasmBlazorCryptoOptions.AssetRoot);
    }

    /// <summary>
    /// Ensure JavaScript module is loaded.
    /// </summary>
    private async ValueTask EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            var modulePath = $"{_sqliteWasmBlazorCryptoOptions.BaseHref}{_sqliteWasmBlazorCryptoOptions.AssetRoot}noble-prf.js";

            await JSHost.ImportAsync("sqliteWasmCryptoPrf", modulePath);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Get JavaScript options object.
    /// </summary>
    private string GetJsOptions()
    {
        var attachment = _options.AuthenticatorAttachment switch
        {
            AuthenticatorAttachment.PLATFORM => "platform",
            AuthenticatorAttachment.CROSS_PLATFORM => "cross-platform",
            AuthenticatorAttachment.ANY => "any",
            _ => "platform"
        };

        var jsOptions = new JsPrfOptions(
            _options.RpName,
            _options.RpId,
            _options.TimeoutMs,
            attachment
        );

        return JsonSerializer.Serialize(jsOptions, PrfJsonContext.Default.JsPrfOptions);
    }

       public async ValueTask<bool> IsPrfSupportedAsync()
    {
        await EnsureInitializedAsync();
        return await JsInterop.IsPrfSupported();
    }

       public async ValueTask<PrfResult<PrfCredential>> RegisterAsync(string? displayName = null)
    {
        await EnsureInitializedAsync();

        var resultJson = await JsInterop.Register(displayName, GetJsOptions());
        var result = JsonSerializer.Deserialize(resultJson, PrfJsonContext.Default.PrfResultPrfCredential);

        return result ?? PrfResult<PrfCredential>.Fail(PrfErrorCode.REGISTRATION_FAILED);
    }

       public async ValueTask<PrfResult<string>> DeriveKeysAsync(string credentialId)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialId);

        // Check cache first
        if (_keyCache.Contains(X25519CacheKey) && _cachedX25519PublicKey is not null)
        {
            return PrfResult<string>.Ok(_cachedX25519PublicKey);
        }

        await EnsureInitializedAsync();

        // Get raw PRF output from JS (WebAuthn)
        var resultJson = await JsInterop.EvaluatePrfOutput(credentialId, _options.Salt, GetJsOptions());
        var result = JsonSerializer.Deserialize(resultJson, PrfJsonContext.Default.PrfResultString);

        if (result is null)
        {
            return PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        if (!result.Success || result.Value is null)
        {
            if (result.Cancelled)
            {
                return PrfResult<string>.UserCancelled();
            }

            return PrfResult<string>.Fail(result.ErrorCode ?? PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        await StoreSeedAndDeriveKeysAsync(result.Value);
        return PrfResult<string>.Ok(_cachedX25519PublicKey!);
    }

       public async ValueTask<PrfResult<(string CredentialId, string PublicKey)>> DeriveKeysDiscoverableAsync()
    {
        await EnsureInitializedAsync();

        // Get raw PRF output from JS (WebAuthn) with discoverable credential
        var resultJson = await JsInterop.EvaluatePrfDiscoverableOutput(_options.Salt, GetJsOptions());
        var result = JsonSerializer.Deserialize(resultJson, PrfJsonContext.Default.PrfResultDiscoverablePrfOutput);

        if (result is null)
        {
            return PrfResult<(string, string)>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        if (!result.Success || result.Value is null)
        {
            if (result.Cancelled)
            {
                return PrfResult<(string, string)>.UserCancelled();
            }

            return PrfResult<(string, string)>.Fail(result.ErrorCode ?? PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        await StoreSeedAndDeriveKeysAsync(result.Value.PrfOutput);
        return PrfResult<(string, string)>.Ok((result.Value.CredentialId, _cachedX25519PublicKey!));
    }

    /// <summary>
    /// Stores the PRF seed + derived X25519 / Ed25519 private keys in the secure cache and
    /// caches the corresponding public keys. Shared by explicit and discoverable ceremonies.
    /// </summary>
    private async ValueTask StoreSeedAndDeriveKeysAsync(string prfOutputBase64)
    {
        var prfSeedBytes = Convert.FromBase64String(prfOutputBase64);
        _keyCache.Store(PrfSeedCacheKey, prfSeedBytes);

        var dualKeys = await _cryptoProvider.DeriveDualKeyPairAsync(prfSeedBytes);

        var x25519PrivateKeyBytes = Convert.FromBase64String(dualKeys.X25519PrivateKey);
        _keyCache.Store(X25519CacheKey, x25519PrivateKeyBytes);
        Array.Clear(x25519PrivateKeyBytes, 0, x25519PrivateKeyBytes.Length);

        var ed25519PrivateKeyBytes = Convert.FromBase64String(dualKeys.Ed25519PrivateKey);
        _keyCache.Store(Ed25519CacheKey, ed25519PrivateKeyBytes);
        Array.Clear(ed25519PrivateKeyBytes, 0, ed25519PrivateKeyBytes.Length);

        Array.Clear(prfSeedBytes, 0, prfSeedBytes.Length);

        _cachedX25519PublicKey = dualKeys.X25519PublicKey;
        _cachedEd25519PublicKey = dualKeys.Ed25519PublicKey;
    }

       public ValueTask<PrfResult<string>> DeriveDomainKeyAsync(string domainId, string context)
    {
        ArgumentException.ThrowIfNullOrEmpty(domainId);
        ArgumentException.ThrowIfNullOrEmpty(context);

        var cacheKey = GetDomainCacheKey(domainId);

        if (_keyCache.Contains(cacheKey))
        {
            return new ValueTask<PrfResult<string>>(PrfResult<string>.Ok(cacheKey));
        }

        if (!_keyCache.Contains(PrfSeedCacheKey))
        {
            // No seed → no gesture on the stack to trigger one from here. UI drives re-auth via
            // PrfModel's commands; this method is purely "derive from what's already cached".
            return new ValueTask<PrfResult<string>>(
                PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED));
        }

        var infoBytes = Encoding.UTF8.GetBytes(context);
        var derived = new byte[32];
        try
        {
            var derivedOk = _keyCache.UseKey(PrfSeedCacheKey, seedSpan =>
            {
                HKDF.DeriveKey(
                    HashAlgorithmName.SHA256,
                    ikm: seedSpan,
                    output: derived,
                    salt: ReadOnlySpan<byte>.Empty,
                    info: infoBytes);
            });

            if (!derivedOk)
            {
                return new ValueTask<PrfResult<string>>(
                    PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED));
            }

            _keyCache.Store(cacheKey, derived);
            return new ValueTask<PrfResult<string>>(PrfResult<string>.Ok(cacheKey));
        }
        finally
        {
            Array.Clear(derived, 0, derived.Length);
        }
    }

       public string? GetCachedPublicKey()
        => _keyCache.Contains(X25519CacheKey) ? _cachedX25519PublicKey : null;

       public bool HasCachedKeys() => _keyCache.Contains(X25519CacheKey);

       public void ClearKeys()
    {
        _keyCache.Clear();
        _cachedX25519PublicKey = null;
        _cachedEd25519PublicKey = null;
    }

       public string? GetEd25519PublicKey()
        => _keyCache.Contains(Ed25519CacheKey) ? _cachedEd25519PublicKey : null;

    private string X25519CacheKey => $"prf-key:{_options.Salt}";
    private string Ed25519CacheKey => $"prf-ed25519-key:{_options.Salt}";
    private string PrfSeedCacheKey => $"prf-seed:{_options.Salt}";

    /// <summary>
    /// Reserved cache-key prefix for keys derived via <see cref="DeriveDomainKeyAsync"/>.
    /// Subscribers to <see cref="KeyExpired"/> can filter on this prefix to recognise
    /// derived-domain-key expirations without ambiguity against the internal slots.
    /// </summary>
    public const string DomainCacheKeyPrefix = "prf-domain:";

    private static string GetDomainCacheKey(string domainId) => DomainCacheKeyPrefix + domainId;

    public ValueTask DisposeAsync()
    {
        ClearKeys();
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// JavaScript interop methods.
    /// WebAuthn/PRF only - crypto operations happen in C#.
    /// </summary>
    private static partial class JsInterop
    {
        [JSImport("isPrfSupported", "sqliteWasmCryptoPrf")]
        public static partial Task<bool> IsPrfSupported();

        [JSImport("register", "sqliteWasmCryptoPrf")]
        public static partial Task<string> Register(string? displayName, string optionsJson);

        [JSImport("evaluatePrfOutput", "sqliteWasmCryptoPrf")]
        public static partial Task<string> EvaluatePrfOutput(string credentialIdBase64, string salt, string optionsJson);

        [JSImport("evaluatePrfDiscoverableOutput", "sqliteWasmCryptoPrf")]
        public static partial Task<string> EvaluatePrfDiscoverableOutput(string salt, string optionsJson);
    }
}
