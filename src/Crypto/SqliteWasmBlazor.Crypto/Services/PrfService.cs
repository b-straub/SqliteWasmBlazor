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
/// and ICryptoProvider for key derivation.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class PrfService : IPrfService, IAsyncDisposable
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

        // Configure-once for the static interop. Idempotent — see CryptoInterop.Configure.
        CryptoInterop.Configure(_sqliteWasmBlazorCryptoOptions.BaseHref, _sqliteWasmBlazorCryptoOptions.AssetRoot);
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

            var modulePath = $"{_sqliteWasmBlazorCryptoOptions.BaseHref}{_sqliteWasmBlazorCryptoOptions.AssetRoot}crypto-bridge.js";

            await JSHost.ImportAsync("sqliteWasmBlazorCryptoPrf", modulePath);
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

        if (result is null)
        {
            return PrfResult<PrfCredential>.Fail(PrfErrorCode.REGISTRATION_FAILED);
        }

        // Pure-credential return; the disk-encryption marker (the on-disk
        // PFAM manifest) is owned by
        // IEncryptedSqliteWasmDatabaseService.EnterEncryptedAsync, which
        // writes it as the last step of the atomic Plain → Encrypted
        // transition. Register here is just "make a passkey + derive keys".
        return result;
    }

       public async ValueTask<PrfResult<string>> DeriveKeysAsync(string credentialId)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialId);

        // Check cache first — JS-side cache is now the source of truth for
        // the derived X25519/Ed25519 bundle; C# only retains the PRF seed
        // for HKDF-based domain-key derivation. The pubkey is non-secret,
        // so honour the cache under every strategy — NONE's one-shot
        // semantics target the *C# seed* (HKDF source), not the public key.
        if (_cryptoProvider.HasCachedKey(JsKeyId)
            && _cachedX25519PublicKey is not null)
        {
            return PrfResult<string>.Ok(_cachedX25519PublicKey);
        }

        await EnsureInitializedAsync();

        // Get raw PRF output from JS (WebAuthn). Deserialized as PrfResult<byte[]>
        // so the seed never lands in a managed string (P21) — System.Text.Json
        // reads the JSON Base64 string directly into a byte[] value.
        var resultJson = await JsInterop.EvaluatePrfOutput(credentialId, _options.Salt, GetJsOptions());
        var result = JsonSerializer.Deserialize(resultJson, PrfJsonContext.Default.PrfResultByteArray);

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
    /// Stores the PRF seed in the secure cache (used by
    /// <see cref="DeriveDomainKeyAsync"/> via the <c>UseKey</c> callback) and
    /// populates the JS-side key cache with the derived X25519/Ed25519/AES
    /// bundle so signing, ECIES decrypt, and symmetric ops can run without
    /// the private key bytes ever crossing the C#↔JS boundary again.
    /// Shared by explicit and discoverable ceremonies.
    /// </summary>
    /// <param name="prfSeedBytes">
    /// 32-byte PRF seed. Caller-owned; this method
    /// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/>s
    /// the buffer in <c>finally</c> after both caches have copied it (P21 —
    /// seed never lands in a managed <see cref="string"/>).
    /// </param>
    private async ValueTask StoreSeedAndDeriveKeysAsync(byte[] prfSeedBytes)
    {
        try
        {
            _keyCache.Store(PrfSeedCacheKey, prfSeedBytes);

            // TtlMs override takes precedence over TtlMinutes so integration
            // tests can drive the JS-side expiry timer within an E2E budget.
            // NONE intentionally maps to null (no JS timer): the JS cache
            // holds non-extractable SubtleCrypto handles + the X25519 priv
            // buffer that every cached crypto op needs to function. A
            // ttlMs=0 here makes the cache *immediately* expired (the
            // isExpired check is `Date.now() >= expiresAt` and storeKeys
            // is async, so any subsequent op sees the entry as expired),
            // breaking signing / decrypt / VFS-key install for the
            // ceremony itself. NONE's hardening is enforced on the C#
            // *seed* cache (one-shot TryGet/UseKey in SecureKeyCache) so
            // HKDF-derived domain keys cannot be regenerated without a
            // fresh auth ceremony. The JS-side handles are session-
            // lifetime; ClearKeys / RemoveCachedKey drops them.
            var ttlMs = _cacheOptions.Strategy switch
            {
                KeyCacheStrategy.TIMED => _cacheOptions.TtlMs ?? _cacheOptions.TtlMinutes * 60_000,
                _ => (int?)null,
            };

            var storeResult = await _cryptoProvider.StoreKeysAsync(JsKeyId, prfSeedBytes, ttlMs);
            if (!storeResult.Success || storeResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"PrfService: failed to populate JS key cache for salt '{_options.Salt}': {storeResult.ErrorCode}");
            }

            _cachedX25519PublicKey = storeResult.Value.X25519PublicKey;
            _cachedEd25519PublicKey = storeResult.Value.Ed25519PublicKey;
        }
        finally
        {
            // _keyCache.Store copies into unmanaged memory; clearing the
            // caller's byte[] here makes sure the seed never lingers on the
            // managed heap. Caller passes ownership in.
            CryptographicOperations.ZeroMemory(prfSeedBytes);
        }
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
            CryptographicOperations.ZeroMemory(derived);
        }
    }

       public string? GetCachedPublicKey()
        => _cryptoProvider.HasCachedKey(JsKeyId) ? _cachedX25519PublicKey : null;

       public bool HasCachedKeys() => _cryptoProvider.HasCachedKey(JsKeyId);

       public void ClearKeys()
    {
        // Drop the C# seed (HKDF source) and the JS-side derived key bundle in one go.
        var hadSeed = _keyCache.Contains(PrfSeedCacheKey);
        _keyCache.Clear();
        _cryptoProvider.RemoveCachedKey(JsKeyId);
        _cachedX25519PublicKey = null;
        _cachedEd25519PublicKey = null;

        // Manual ClearKeys is semantically a session end — same effect as
        // TTL expiry — so emit the same KeyExpired signal that
        // AuthenticationModel.OnSessionExpired and EncryptedDiskLifecycle
        // already subscribe to. SecureKeyCache.Clear is a bulk wipe and
        // intentionally silent (it doesn't know which keys matter to which
        // subscriber); PrfService synthesizes the per-seed expiry event
        // here because it owns the seed cache key. Skip when there was no
        // seed to begin with — avoids bogus "session ended" notifications
        // on a cold ClearKeys (e.g. during boot).
        if (hadSeed)
        {
            _keyCache.NotifyKeyCleared(PrfSeedCacheKey);
        }
    }

       public string? GetEd25519PublicKey()
        => _cryptoProvider.HasCachedKey(JsKeyId) ? _cachedEd25519PublicKey : null;

       public async ValueTask<PrfResult<byte[]>> DecryptAsymmetricToBytesAsync(
           AsymmetricEncryptedData asymmetricEncrypted)
       {
           if (!_cryptoProvider.SupportsKeyIdOperations)
           {
               return PrfResult<byte[]>.Fail(PrfErrorCode.NOT_SUPPORTED);
           }

           return await _cryptoProvider.DecryptAsymmetricWithKeyIdToBytesAsync(
               asymmetricEncrypted,
               JsKeyId);
       }

    private string PrfSeedCacheKey => $"prf-seed:{_options.Salt}";

    /// <summary>
    /// JS-side key cache identifier — the dual key bundle (X25519 priv as
    /// <see cref="System.Runtime.InteropServices.JavaScript.JSType"/> Uint8Array,
    /// Ed25519 + AES as non-extractable <c>SubtleCrypto</c> CryptoKey objects)
    /// is stored under this id by <see cref="StoreSeedAndDeriveKeysAsync"/>.
    /// </summary>
    private string JsKeyId => PrfKeyConventions.GetJsKeyId(_options.Salt);

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
        [JSImport("isPrfSupported", "sqliteWasmBlazorCryptoPrf")]
        public static partial Task<bool> IsPrfSupported();

        [JSImport("register", "sqliteWasmBlazorCryptoPrf")]
        public static partial Task<string> Register(string? displayName, string optionsJson);

        [JSImport("evaluatePrfOutput", "sqliteWasmBlazorCryptoPrf")]
        public static partial Task<string> EvaluatePrfOutput(string credentialIdBase64, string salt, string optionsJson);

        [JSImport("evaluatePrfDiscoverableOutput", "sqliteWasmBlazorCryptoPrf")]
        public static partial Task<string> EvaluatePrfDiscoverableOutput(string salt, string optionsJson);
    }
}
