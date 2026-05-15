using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using SqliteWasmBlazor.Crypto.Configuration;

namespace SqliteWasmBlazor.Crypto.Interop;

/// <summary>
/// JavaScript interop for SubtleCrypto + @awasm/noble hybrid crypto operations.
/// Sync operations return packed binary as Base64 strings (no JSON overhead).
/// Async operations return packed binary as Base64 strings via Task&lt;string&gt;.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class CryptoInterop
{
    private const string ModuleName = "sqliteWasmBlazorCryptoBridge";
    private static readonly SemaphoreSlim InitSemaphore = new(1, 1);
    private static bool _initialized;
    private static string? _baseHref;
    private static string? _assetRoot;

    /// <summary>
    /// Records the resolved <see cref="SqliteWasmBlazorCryptoOptions.BaseHref"/> and
    /// <see cref="SqliteWasmBlazorCryptoOptions.AssetRoot"/> used to locate the JS module.
    /// First call wins (services share a single <c>IOptions&lt;SqliteWasmBlazorCryptoOptions&gt;</c>);
    /// subsequent calls are no-ops.
    /// </summary>
    internal static void Configure(string baseHref, string assetRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseHref);
        ArgumentException.ThrowIfNullOrEmpty(assetRoot);

        if (_baseHref is not null)
        {
            return;
        }

        _baseHref = baseHref;
        _assetRoot = assetRoot;
    }

    /// <summary>
    /// Sync probe for the JS-module load state. Sync sites
    /// (<see cref="HasKey"/>, <see cref="RemoveKeys"/>, <see cref="HasVapidKey"/>,
    /// <see cref="ClearVapidKey"/>) must check this first because the underlying
    /// <c>JSImport</c> aborts the WASM runtime on a not-imported assert,
    /// which crashes any caller running before any async ceremony has had a
    /// chance to load the module.
    /// </summary>
    public static bool IsInitialized => _initialized;

    public static async ValueTask EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        if (_baseHref is null || _assetRoot is null)
        {
            throw new InvalidOperationException(
                "SqliteWasmBlazor.Crypto is not configured. Call services.AddSqliteWasmBlazorCrypto(...) before " +
                "resolving any crypto service. For sub-path or browser-extension deployments " +
                "set SqliteWasmBlazorCryptoOptions.BaseHref / SqliteWasmBlazorCryptoOptions.AssetRoot.");
        }

        await InitSemaphore.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            var modulePath = $"{_baseHref}{_assetRoot}crypto-bridge.js";

            await JSHost.ImportAsync(ModuleName, modulePath);
            _initialized = true;
        }
        finally
        {
            InitSemaphore.Release();
        }
    }

    // ============================================================
    // ED25519 — sign returns Base64 of signature(64)
    // ============================================================

    /// <summary>
    /// Sign with Ed25519. The private key crosses as a binary <c>MemoryView</c> so no
    /// immutable Base64 string holds the secret on the JS heap.
    /// </summary>
    [JSImport("ed25519SignB64", ModuleName)]
    public static partial Task<string> Ed25519SignAsync(
        string messageBase64,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> privateKey);

    [JSImport("ed25519VerifyB64", ModuleName)]
    public static partial Task<bool> Ed25519VerifyAsync(string signatureBase64, string messageBase64, string publicKeyBase64);

    // ============================================================
    // DUAL KEY — JS writes [x25519Priv(32)|x25519Pub(32)|ed25519Priv(32)|ed25519Pub(32)]
    //           directly into the caller-allocated 128-byte output buffer.
    // ============================================================

    /// <summary>
    /// Derive [x25519 + ed25519] keypair into a caller-allocated 128-byte
    /// output buffer. Both seed and output cross as binary <c>MemoryView</c>s —
    /// the seed never lands in an immutable Base64 string on the JS heap;
    /// the priv keys in the output never land in one on the C# heap either.
    /// Caller layout: <c>[x25519Priv(32)|x25519Pub(32)|ed25519Priv(32)|ed25519Pub(32)]</c>.
    /// </summary>
    [JSImport("deriveDualKeyPair_into", ModuleName)]
    public static partial Task DeriveDualKeyPairIntoAsync(
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> seed,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> output);

    // ============================================================
    // AES-GCM — encrypt returns Base64 of [nonce(12)|ciphertext], decrypt returns Base64 of plaintext
    // ============================================================

    /// <summary>
    /// AES-GCM encrypt. Both plaintext and key cross as binary <c>MemoryView</c>s — no
    /// immutable Base64 string holds the plaintext (which may itself be a wrapped
    /// content key) or the wrapping key on the JS heap.
    /// </summary>
    [JSImport("encryptAesGcmB64", ModuleName)]
    public static partial Task<string> EncryptAesGcmAsync(
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> plaintext,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> key,
        string? aad = null);

    /// <summary>
    /// AES-GCM decrypt. The key crosses as a binary <c>MemoryView</c> so no immutable
    /// Base64 string holds the secret on the JS heap.
    /// </summary>
    [JSImport("decryptAesGcmB64", ModuleName)]
    public static partial Task<string> DecryptAesGcmAsync(
        string ciphertextBase64,
        string nonceBase64,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> key,
        string? aad = null);

    /// <summary>
    /// Bytes-out async variant of <see cref="DecryptAesGcmAsync"/>:
    /// JS writes the plaintext directly into a caller-allocated MemoryView
    /// output instead of returning a Base64 string. Caller sizes <c>output</c>
    /// to the expected plaintext length (= ciphertext bytes − 16-byte AES-GCM tag).
    /// SYSLIB1072 forces <see cref="ArraySegment{T}"/> instead of <see cref="Span{T}"/>
    /// on the Task-returning signature. Used by
    /// <see cref="SubtleCryptoProvider.UnwrapContentKeyAsync"/> on every CEK unwrap.
    /// </summary>
    [JSImport("decryptAesGcm_into", ModuleName)]
    public static partial Task DecryptAesGcmIntoAsync(
        string ciphertextBase64,
        string nonceBase64,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> key,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> output,
        string? aad = null);

    // ============================================================
    // ECIES — encrypt returns Base64 of [ephPubKey(32)|nonce(12)|ciphertext], decrypt returns Base64
    // ============================================================

    [JSImport("encryptAsymmetricB64", ModuleName)]
    public static partial Task<string> EncryptAsymmetricAesGcmAsync(string plaintextBase64, string recipientPublicKeyBase64);

    /// <summary>
    /// Bytes-out ECIES decrypt: writes plaintext directly into a caller-allocated
    /// <c>output</c> MemoryView. Used for the K_wrap unwrap on the disk-import
    /// path. The private key crosses as a binary <c>MemoryView</c> on input;
    /// no Base64 string carrying the secret on either heap (P21).
    /// </summary>
    [JSImport("decryptAsymmetric_into", ModuleName)]
    public static partial Task DecryptAsymmetricIntoAsync(
        string ephemeralPublicKeyBase64,
        string ciphertextBase64,
        string nonceBase64,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> privateKey,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> output);

    /// <summary>
    /// ECIES encrypt where plaintext crosses as a binary <c>MemoryView</c>
    /// (P21 — for secret-key plaintext). The wrap-key / PRF-seed bytes never
    /// land in an immutable Base64 string on the C# or JS heap. JS slices
    /// the MemoryView into a real Uint8Array; caller zeros its source byte[]
    /// in <c>finally</c>.
    /// </summary>
    [JSImport("encryptAsymmetricFromBytesB64", ModuleName)]
    public static partial Task<string> EncryptAsymmetricFromBytesAesGcmAsync(
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> plaintext,
        string recipientPublicKeyBase64);

    // ============================================================
    // KEY DERIVATION — JS writes derived bytes directly into caller-allocated
    //                  MemoryView output. No Base64 string carrying the
    //                  secret on either heap (P21).
    // ============================================================

    /// <summary>
    /// Derive an HKDF wrapping key from an X25519 private key + recipient
    /// public key, into a caller-allocated 32-byte output buffer. Own private
    /// key and output both cross as binary <c>MemoryView</c>s.
    /// </summary>
    [JSImport("deriveWrappingKey_into", ModuleName)]
    public static partial Task DeriveWrappingKeyIntoAsync(
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> ownPrivateKey,
        string recipientPublicKeyBase64,
        string context,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> output);

    // ============================================================
    // UTILITY
    // ============================================================

    /// <summary>
    /// Fill a caller-allocated buffer with cryptographically secure random
    /// bytes. Random output is secret-equivalent (used for CEKs, K_wrap,
    /// salts) so it crosses as a writable <c>MemoryView</c> rather than a
    /// Base64 return string.
    /// </summary>
    [JSImport("generateRandomBytes_into", ModuleName)]
    public static partial void GenerateRandomBytesInto(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> output);

    [JSImport("isSupported", ModuleName)]
    public static partial bool IsSupported();

    // ============================================================
    // KEY CACHE — storeKeys returns Base64 of [x25519Pub(32)|ed25519Pub(32)]
    // ============================================================

    /// <summary>
    /// Store and derive keys from a PRF seed. The seed crosses as a binary
    /// <c>MemoryView</c> so no immutable Base64 string holds the seed on the
    /// JS heap. Caller owns the source <see cref="ArraySegment{T}"/> lifecycle.
    /// </summary>
    [JSImport("storeKeysB64", ModuleName)]
    public static partial Task<string> StoreKeysAsync(
        string keyId,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> seed,
        int? ttlMs);

    [JSImport("getPublicKeysB64", ModuleName)]
    public static partial string GetPublicKeys(string keyId);

    [JSImport("hasKey", ModuleName)]
    public static partial bool HasKey(string keyId);

    [JSImport("removeKeys", ModuleName)]
    public static partial void RemoveKeys(string keyId);

    [JSImport("clearAllKeys", ModuleName)]
    public static partial void ClearAllKeys();

    // ============================================================
    // CACHED KEY OPERATIONS — returns packed Base64
    // ============================================================

    [JSImport("signWithCachedKeyB64", ModuleName)]
    public static partial Task<string> SignWithCachedKeyAsync(string keyId, string messageBase64);

    [JSImport("decryptAsymmetricCachedB64", ModuleName)]
    public static partial Task<string> DecryptAsymmetricCachedAesGcmAsync(
        string keyId, string ephemeralPublicKeyBase64, string ciphertextBase64, string nonceBase64);

    [JSImport("decryptAsymmetricCached_into", ModuleName)]
    public static partial Task DecryptAsymmetricCachedIntoAsync(
        string keyId,
        string ephemeralPublicKeyBase64,
        string ciphertextBase64,
        string nonceBase64,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> output);

    // ============================================================
    // VAPID + WEBPUSH — Returns packed Base64
    // ============================================================

    /// <summary>
    /// Generate VAPID ECDSA P-256 keypair.
    /// Returns Base64 of [publicKey(65) | privateKeyPkcs8(N)].
    /// Also caches the CryptoKey in JS for immediate signing.
    /// </summary>
    /// <summary>
    /// Generate a VAPID ECDSA P-256 keypair and write the packed
    /// <c>[publicKey(65)|privateKeyPkcs8(N)]</c> directly into the caller-
    /// allocated <c>output</c> buffer. Returns the number of bytes actually
    /// written (variable due to the variable-length PKCS8 encoding). Also
    /// caches the CryptoKey in JS for immediate signing.
    /// </summary>
    [JSImport("generateVapidKeyPair_into", ModuleName)]
    public static partial Task<int> GenerateVapidKeyPairIntoAsync(
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> output);

    /// <summary>
    /// Import VAPID keypair from stored components and cache for signing.
    /// The pkcs8 private key crosses as a binary <c>MemoryView</c> so no
    /// immutable Base64 string carries the secret on either heap (P21).
    /// </summary>
    [JSImport("importVapidKeyPairB64", ModuleName)]
    public static partial Task<bool> ImportVapidKeyPairAsync(
        string publicKeyBase64,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> pkcs8PrivateKey);

    /// <summary>
    /// Send an encrypted push notification via server-side proxy (CORS bypass).
    /// All crypto done client-side, proxy just forwards to push service.
    /// Returns a JSON-encoded <c>WebPushResult</c>: <c>{ success, status, endpoint, gone, reason, responseBody }</c>.
    /// </summary>
    [JSImport("sendPushNotificationB64", ModuleName)]
    public static partial Task<string> SendPushNotificationAsync(
        string endpoint, string p256dhBase64, string authBase64,
        string payloadBase64, string subject, string proxyUrl, string apiKey, int ttl);

    /// <summary>
    /// Check if a VAPID key is currently loaded.
    /// </summary>
    [JSImport("hasVapidKey", ModuleName)]
    public static partial bool HasVapidKey();

    /// <summary>
    /// Clear cached VAPID key from memory.
    /// </summary>
    [JSImport("clearVapidKey", ModuleName)]
    public static partial void ClearVapidKey();
}
