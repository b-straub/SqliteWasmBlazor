using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace SqliteWasmBlazor;

/// <summary>
/// JSImport interop to crypto-layer.js on the main thread.
/// Thin wrapper — same pattern as BlazorPRF.Noble.Crypto's NobleInterop.
///
/// For production WebAuthn PRF flow: bridge handles seed → worker directly.
/// This interop is for C# direct crypto access (testing, UI-level signing, Phase 6).
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class CryptoInterop
{
    private const string ModuleName = "sqliteWasmCrypto";
    private static readonly SemaphoreSlim InitSemaphore = new(1, 1);
    private static bool _initialized;

    public static async ValueTask EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await InitSemaphore.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            await JSHost.ImportAsync("CryptoHelper",
                "data:text/javascript,export function getBaseHref() { return document.querySelector('base')?.getAttribute('href') || '/'; }");
            var baseHref = GetBaseHref();
            var modulePath = $"{baseHref}_content/SqliteWasmBlazor/crypto-layer.js";

            await JSHost.ImportAsync(ModuleName, modulePath);
            _initialized = true;
        }
        finally
        {
            InitSemaphore.Release();
        }
    }

    [JSImport("getBaseHref", "CryptoHelper")]
    private static partial string GetBaseHref();

    // ============================================================
    // KEY CACHE MANAGEMENT
    // ============================================================

    [JSImport("storeKeys", ModuleName)]
    public static partial Task<string> StoreKeysAsync(string keyId, string seedBase64, int? ttlMs);

    [JSImport("getPublicKeys", ModuleName)]
    public static partial string GetPublicKeys(string keyId);

    [JSImport("hasKey", ModuleName)]
    public static partial bool HasKey(string keyId);

    [JSImport("removeKeys", ModuleName)]
    public static partial void RemoveKeys(string keyId);

    [JSImport("clearAllKeys", ModuleName)]
    public static partial void ClearAllKeys();

    // ============================================================
    // CACHED KEY OPERATIONS
    // ============================================================

    [JSImport("signWithCachedKey", ModuleName)]
    public static partial string SignWithCachedKey(string keyId, string messageBase64);

    [JSImport("encryptSymmetricCachedAesGcm", ModuleName)]
    public static partial Task<string> EncryptSymmetricCachedAesGcmAsync(string keyId, string plaintextBase64);

    [JSImport("decryptSymmetricCachedAesGcm", ModuleName)]
    public static partial Task<string> DecryptSymmetricCachedAesGcmAsync(string keyId, string ciphertextBase64, string nonceBase64);

    // ============================================================
    // ED25519 SIGNING
    // ============================================================

    [JSImport("ed25519Sign", ModuleName)]
    public static partial string Ed25519Sign(string messageBase64, string privateKeyBase64);

    [JSImport("ed25519Verify", ModuleName)]
    public static partial bool Ed25519Verify(string messageBase64, string signatureBase64, string publicKeyBase64);

    // ============================================================
    // AES-GCM SYMMETRIC ENCRYPTION
    // ============================================================

    [JSImport("encryptAesGcm", ModuleName)]
    public static partial Task<string> EncryptAesGcmAsync(string plaintextBase64, string keyBase64);

    [JSImport("decryptAesGcm", ModuleName)]
    public static partial Task<string> DecryptAesGcmAsync(string ciphertextBase64, string nonceBase64, string keyBase64);

    // ============================================================
    // ECIES ASYMMETRIC ENCRYPTION (X25519 + AES-GCM)
    // ============================================================

    [JSImport("encryptAsymmetricAesGcm", ModuleName)]
    public static partial Task<string> EncryptAsymmetricAesGcmAsync(string plaintextBase64, string recipientPublicKeyBase64);

    [JSImport("decryptAsymmetricAesGcm", ModuleName)]
    public static partial Task<string> DecryptAsymmetricAesGcmAsync(
        string ephemeralPublicKeyBase64, string ciphertextBase64,
        string nonceBase64, string privateKeyBase64);

    // ============================================================
    // KEY DERIVATION
    // ============================================================

    [JSImport("generateRandomBytes", ModuleName)]
    public static partial string GenerateRandomBytes(int length);

    [JSImport("isSupported", ModuleName)]
    public static partial bool IsSupported();

    [JSImport("deriveHkdfKey", ModuleName)]
    public static partial string DeriveHkdfKey(string seedBase64, string domain);

    [JSImport("deriveDualKeyPair", ModuleName)]
    public static partial string DeriveDualKeyPair(string seedBase64);
}
