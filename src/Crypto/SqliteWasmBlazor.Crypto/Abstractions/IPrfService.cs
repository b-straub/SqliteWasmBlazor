using R3;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Service for WebAuthn PRF operations.
/// </summary>
public interface IPrfService
{
    /// <summary>
    /// The configured key caching strategy.
    /// </summary>
    KeyCacheStrategy CacheStrategy { get; }

    /// <summary>
    /// The app-wide PRF salt used by every ceremony on this service. Sourced from
    /// <see cref="PrfOptions.Salt"/>. One salt per app — domain separation for derived keys
    /// lives in the HKDF <c>context</c> argument of <see cref="DeriveDomainKeyAsync"/>, not
    /// in a per-call salt.
    /// </summary>
    string Salt { get; }

    /// <summary>
    /// Observable that emits the cache key when C#-side cached entries expire due to TTL.
    /// Internal slots use the reserved <c>prf-seed:{salt}</c> prefix for the PRF seed and
    /// <c>prf-domain:{domainId}</c> for HKDF-derived domain keys. The JS-side derived
    /// key bundle (<c>prf-keys:{salt}</c>) maintains its own TTL in lockstep but does
    /// not surface through this observable — subscribing to the seed key is enough to
    /// detect session-end, since session = seed cached.
    /// </summary>
    Observable<string> KeyExpired { get; }

    /// <summary>
    /// Check if PRF extension is supported on this platform.
    /// </summary>
    ValueTask<bool> IsPrfSupportedAsync();

    /// <summary>
    /// Register a new credential with PRF support.
    /// </summary>
    /// <param name="displayName">Optional display name shown in platform passkey manager. If null, platform generates one.</param>
    /// <returns>The created credential or error</returns>
    ValueTask<PrfResult<PrfCredential>> RegisterAsync(string? displayName = null);

    /// <summary>
    /// Derive keys from a specific credential. Uses the app-wide <see cref="Salt"/>.
    /// Keys are cached in unmanaged memory according to the configured cache strategy.
    /// </summary>
    /// <param name="credentialId">The credential ID (Base64)</param>
    /// <returns>The public key (private key is cached internally)</returns>
    ValueTask<PrfResult<string>> DeriveKeysAsync(string credentialId);

    /// <summary>
    /// Derive keys using discoverable credential (user selects). Uses the app-wide
    /// <see cref="Salt"/>. Keys are cached in unmanaged memory according to the configured
    /// cache strategy.
    /// </summary>
    /// <returns>The credential ID and public key</returns>
    ValueTask<PrfResult<(string CredentialId, string PublicKey)>> DeriveKeysDiscoverableAsync();

    /// <summary>
    /// Get the cached X25519 public key, if available.
    /// </summary>
    /// <returns>The public key (Base64) or null if not cached</returns>
    string? GetCachedPublicKey();

    /// <summary>
    /// Check if keys are cached.
    /// </summary>
    /// <returns>True if keys are cached and valid</returns>
    bool HasCachedKeys();

    /// <summary>
    /// Get the cached Ed25519 signing public key, if available.
    /// </summary>
    /// <returns>The Ed25519 public key (Base64) or null if not cached</returns>
    string? GetEd25519PublicKey();

    /// <summary>
    /// Clear all cached keys.
    /// </summary>
    void ClearKeys();

    /// <summary>
    /// HKDF-SHA256 derive a 32-byte domain-separated key from the cached PRF seed and store it
    /// in the secure key cache under the reserved <c>prf-domain:{domainId}</c> namespace.
    /// </summary>
    /// <remarks>
    /// Requires a prior successful <see cref="DeriveKeysAsync"/> or
    /// <see cref="DeriveKeysDiscoverableAsync"/> — if the PRF seed is not cached, returns
    /// <see cref="PrfErrorCode.KEY_DERIVATION_FAILED"/>. Re-establishing the session is a UI
    /// concern (gesture-driven) handled through <c>PrfModel</c>'s commands; this method never
    /// triggers a ceremony on its own.
    ///
    /// HKDF salt is empty; the PRF seed is already credential-scoped. Callers supply a short
    /// <paramref name="domainId"/> (their own identifier, e.g. <c>"sqlite-vfs:users.db"</c>)
    /// and the <paramref name="context"/> string (HKDF <c>info</c>) for domain separation.
    /// The reserved <c>prf-domain:</c> prefix prevents collisions with the internal
    /// <c>prf-seed:</c> slot. The returned
    /// <see cref="PrfResult{T}.Value"/> is the fully qualified cache key — hand it to
    /// <see cref="ISecureKeyCache.UseKey(string, ReadOnlySpanAction{byte})"/> for scoped span
    /// access, and filter <see cref="KeyExpired"/> with <c>StartsWith("prf-domain:")</c> to
    /// recognise derived-domain-key expirations.
    /// </remarks>
    /// <param name="domainId">Caller's domain identifier. Combined with the reserved prefix to form the cache key.</param>
    /// <param name="context">HKDF <c>info</c> string — the caller's domain-separation label.</param>
    /// <returns>On success, the fully qualified cache key (<c>prf-domain:{domainId}</c>).</returns>
    ValueTask<PrfResult<string>> DeriveDomainKeyAsync(string domainId, string context);

    /// <summary>
    /// ECIES-decrypts secret bytes using the PRF-backed cached X25519 key
    /// without exporting the private key into managed memory.
    /// </summary>
    /// <remarks>
    /// Used by the asymmetric disk-import path to unwrap the per-export
    /// wrap key. Implementations route through the provider's keyId cache
    /// so private material remains on the provider side; caller owns and
    /// zeroes the returned plaintext bytes.
    /// </remarks>
    ValueTask<PrfResult<byte[]>> DecryptAsymmetricToBytesAsync(
        AsymmetricEncryptedData asymmetricEncrypted);
}
