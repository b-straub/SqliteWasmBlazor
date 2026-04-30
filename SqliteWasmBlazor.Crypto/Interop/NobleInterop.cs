using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using SqliteWasmBlazor.Crypto.Configuration;

namespace SqliteWasmBlazor.Crypto.Interop;

/// <summary>
/// JavaScript interop for Noble.js + SubtleCrypto hybrid crypto operations.
/// Sync operations return packed binary as Base64 strings (no JSON overhead).
/// Async operations return packed binary as Base64 strings via Task&lt;string&gt;.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class NobleInterop
{
    private const string ModuleName = "sqliteWasmBlazorCryptoNoble";
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

            var modulePath = $"{_baseHref}{_assetRoot}noble-prf.js";

            await JSHost.ImportAsync(ModuleName, modulePath);
            _initialized = true;
        }
        finally
        {
            InitSemaphore.Release();
        }
    }

    // ============================================================
    // X25519 — Returns Base64 of: [privKey(32) | pubKey(32)]
    // ============================================================

    [JSImport("generateX25519KeyPairB64", ModuleName)]
    public static partial string GenerateX25519KeyPair();

    [JSImport("getX25519PublicKeyB64", ModuleName)]
    public static partial string GetX25519PublicKey(string privateKeyBase64);

    [JSImport("deriveX25519KeyPairB64", ModuleName)]
    public static partial string DeriveX25519KeyPair(string seedBase64);

    // ============================================================
    // ED25519 — sign returns Base64 of signature(64)
    // ============================================================

    [JSImport("generateEd25519KeyPairB64", ModuleName)]
    public static partial string GenerateEd25519KeyPair();

    [JSImport("getEd25519PublicKeyB64", ModuleName)]
    public static partial string GetEd25519PublicKey(string privateKeyBase64);

    [JSImport("deriveEd25519KeyPairB64", ModuleName)]
    public static partial string DeriveEd25519KeyPair(string seedBase64);

    /// <summary>
    /// Sign with Ed25519. The private key crosses as a binary <c>MemoryView</c> so no
    /// immutable Base64 string holds the secret on the JS heap.
    /// </summary>
    [JSImport("ed25519SignB64", ModuleName)]
    public static partial string Ed25519Sign(
        string messageBase64,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> privateKey);

    [JSImport("ed25519VerifyB64", ModuleName)]
    public static partial bool Ed25519Verify(string signatureBase64, string messageBase64, string publicKeyBase64);

    // ============================================================
    // DUAL KEY — Returns Base64 of: [x25519Priv(32)|x25519Pub(32)|ed25519Priv(32)|ed25519Pub(32)]
    // ============================================================

    [JSImport("deriveDualKeyPairB64", ModuleName)]
    public static partial string DeriveDualKeyPair(string seedBase64);

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

    // ============================================================
    // ECIES — encrypt returns Base64 of [ephPubKey(32)|nonce(12)|ciphertext], decrypt returns Base64
    // ============================================================

    [JSImport("encryptAsymmetricB64", ModuleName)]
    public static partial Task<string> EncryptAsymmetricAesGcmAsync(string plaintextBase64, string recipientPublicKeyBase64);

    /// <summary>
    /// Decrypt ECIES envelope. The private key crosses as a binary <c>MemoryView</c> so
    /// no immutable Base64 string holds the secret on the JS heap. Uses
    /// <see cref="ArraySegment{T}"/> because <c>Span&lt;byte&gt;</c> is not supported on
    /// Task-returning JSImport methods (SYSLIB1072).
    /// </summary>
    [JSImport("decryptAsymmetricB64", ModuleName)]
    public static partial Task<string> DecryptAsymmetricAesGcmAsync(
        string ephemeralPublicKeyBase64,
        string ciphertextBase64,
        string nonceBase64,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> privateKey);

    // ============================================================
    // KEY DERIVATION — Returns Base64 of key bytes
    // ============================================================

    [JSImport("deriveHkdfKeyB64", ModuleName)]
    public static partial string DeriveHkdfKey(string seedBase64, string domain);

    [JSImport("deriveWrappingKeyB64", ModuleName)]
    public static partial string DeriveWrappingKey(string ownPrivateKeyBase64, string recipientPublicKeyBase64, string context);

    // ============================================================
    // UTILITY
    // ============================================================

    [JSImport("generateRandomBytesB64", ModuleName)]
    public static partial string GenerateRandomBytes(int length);

    [JSImport("isSupported", ModuleName)]
    public static partial bool IsSupported();

    // ============================================================
    // KEY CACHE — storeKeys returns Base64 of [x25519Pub(32)|ed25519Pub(32)]
    // ============================================================

    [JSImport("storeKeysB64", ModuleName)]
    public static partial Task<string> StoreKeysAsync(string keyId, string seedBase64, int? ttlMs);

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

    [JSImport("encryptSymmetricCachedB64", ModuleName)]
    public static partial Task<string> EncryptSymmetricCachedAesGcmAsync(string keyId, string plaintextBase64, string? aad = null);

    [JSImport("decryptSymmetricCachedB64", ModuleName)]
    public static partial Task<string> DecryptSymmetricCachedAesGcmAsync(string keyId, string ciphertextBase64, string nonceBase64, string? aad = null);

    [JSImport("decryptAsymmetricCachedB64", ModuleName)]
    public static partial Task<string> DecryptAsymmetricCachedAesGcmAsync(
        string keyId, string ephemeralPublicKeyBase64, string ciphertextBase64, string nonceBase64);

    // ============================================================
    // VAPID + WEBPUSH — Returns packed Base64
    // ============================================================

    /// <summary>
    /// Generate VAPID ECDSA P-256 keypair.
    /// Returns Base64 of [publicKey(65) | privateKeyPkcs8(N)].
    /// Also caches the CryptoKey in JS for immediate signing.
    /// </summary>
    [JSImport("generateVapidKeyPairB64", ModuleName)]
    public static partial Task<string> GenerateVapidKeyPairAsync();

    /// <summary>
    /// Import VAPID keypair from stored components and cache for signing.
    /// </summary>
    [JSImport("importVapidKeyPairB64", ModuleName)]
    public static partial Task<bool> ImportVapidKeyPairAsync(string publicKeyBase64, string pkcs8PrivateKeyBase64);

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
