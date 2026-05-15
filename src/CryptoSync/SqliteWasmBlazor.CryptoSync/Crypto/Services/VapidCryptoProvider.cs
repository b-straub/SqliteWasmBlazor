using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Interop;
using SqliteWasmBlazor.CryptoSync.Crypto.Json;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// VAPID ECDSA P-256 operations via SubtleCrypto.
/// Wraps CryptoInterop VAPID functions behind IVapidCryptoProvider.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class VapidCryptoProvider : IVapidCryptoProvider
{
    public VapidCryptoProvider(IOptions<SqliteWasmBlazorCryptoOptions> options)
    {
        var resolved = options.Value;
        // Configure-once for the static interop. Idempotent — see CryptoInterop.Configure.
        CryptoInterop.Configure(resolved.BaseHref, resolved.AssetRoot);
    }

    public async ValueTask EnsureInitializedAsync()
    {
        await CryptoInterop.EnsureInitializedAsync();
    }

    public async Task<byte[]> GenerateKeyPairAsync()
    {
        await CryptoInterop.EnsureInitializedAsync();
        // P-256 packed [pubkey(65) | pkcs8PrivKey(~138)]. Allocate generously
        // (256 bytes — well above the typical 200) and trim to the actual
        // bytes-written count returned by the JS bridge. Bytes-out path —
        // no Base64 string carrying the PKCS8 priv key on either heap (P21).
        var buffer = new byte[256];
        var written = await CryptoInterop.GenerateVapidKeyPairIntoAsync(new ArraySegment<byte>(buffer));
        if (written < 0)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            throw new InvalidOperationException(
                "VapidCryptoProvider: GenerateVapidKeyPair output exceeded the 256-byte buffer.");
        }
        var result = new byte[written];
        buffer.AsSpan(0, written).CopyTo(result);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        return result;
    }

    public async Task<bool> ImportKeyPairAsync(string publicKeyBase64, ReadOnlyMemory<byte> pkcs8PrivateKey)
    {
        await CryptoInterop.EnsureInitializedAsync();

        if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(pkcs8PrivateKey, out ArraySegment<byte> pkcs8Segment))
        {
            return false;
        }

        return await CryptoInterop.ImportVapidKeyPairAsync(publicKeyBase64, pkcs8Segment);
    }

    // Sync paths must guard on CryptoInterop.IsInitialized — the underlying
    // [JSImport] aborts the WASM runtime if the JS module isn't loaded yet.
    public bool IsLoaded => CryptoInterop.IsInitialized && CryptoInterop.HasVapidKey();

    public void ClearKey()
    {
        if (!CryptoInterop.IsInitialized)
        {
            return;
        }
        CryptoInterop.ClearVapidKey();
    }

    public async Task<PushSendResult> SendPushNotificationAsync(
        string endpoint, string p256dhBase64, string authBase64,
        string payloadBase64, string subject, string proxyUrl, string apiKey, int ttl)
    {
        await CryptoInterop.EnsureInitializedAsync();
        var resultJson = await CryptoInterop.SendPushNotificationAsync(
            endpoint, p256dhBase64, authBase64, payloadBase64, subject, proxyUrl, apiKey, ttl);
        return Parse(resultJson, endpoint);
    }

    private static PushSendResult Parse(string resultJson, string endpoint)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(
                resultJson, CryptoSyncJsonContext.Default.PushSendResult);
            return parsed ?? PushSendResult.Failure(endpoint);
        }
        catch (JsonException)
        {
            return PushSendResult.Failure(endpoint);
        }
    }
}
