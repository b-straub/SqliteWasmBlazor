using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Json;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Interop;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// VAPID ECDSA P-256 operations via Noble.js + SubtleCrypto.
/// Wraps NobleInterop VAPID functions behind IVapidCryptoProvider.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class VapidCryptoProvider : IVapidCryptoProvider
{
    public VapidCryptoProvider(IOptions<SqliteWasmBlazorCryptoOptions> options)
    {
        var resolved = options.Value;
        // Configure-once for the static interop. Idempotent — see NobleInterop.Configure.
        NobleInterop.Configure(resolved.BaseHref, resolved.AssetRoot);
    }

    public async ValueTask EnsureInitializedAsync()
    {
        await NobleInterop.EnsureInitializedAsync();
    }

    public async Task<byte[]> GenerateKeyPairAsync()
    {
        await NobleInterop.EnsureInitializedAsync();
        var packedBase64 = await NobleInterop.GenerateVapidKeyPairAsync();
        return Convert.FromBase64String(packedBase64);
    }

    public async Task<bool> ImportKeyPairAsync(string publicKeyBase64, string pkcs8PrivateKeyBase64)
    {
        await NobleInterop.EnsureInitializedAsync();
        return await NobleInterop.ImportVapidKeyPairAsync(publicKeyBase64, pkcs8PrivateKeyBase64);
    }

    public bool IsLoaded => NobleInterop.HasVapidKey();

    public void ClearKey() => NobleInterop.ClearVapidKey();

    public async Task<PushSendResult> SendPushNotificationAsync(
        string endpoint, string p256dhBase64, string authBase64,
        string payloadBase64, string subject, string proxyUrl, string apiKey, int ttl)
    {
        await NobleInterop.EnsureInitializedAsync();
        var resultJson = await NobleInterop.SendPushNotificationAsync(
            endpoint, p256dhBase64, authBase64, payloadBase64, subject, proxyUrl, apiKey, ttl);
        return Parse(resultJson, endpoint);
    }

    private static PushSendResult Parse(string resultJson, string endpoint)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(
                resultJson, SharedJsonContext.Default.PushSendResult);
            return parsed ?? PushSendResult.Failure(endpoint);
        }
        catch (JsonException)
        {
            return PushSendResult.Failure(endpoint);
        }
    }
}
