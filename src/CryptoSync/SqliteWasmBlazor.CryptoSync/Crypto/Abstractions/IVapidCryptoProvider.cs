using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Abstractions;

/// <summary>
/// Abstraction for VAPID ECDSA P-256 operations (WebPush identity).
/// Manages keypair generation, import, and in-memory CryptoKey cache for signing.
/// </summary>
public interface IVapidCryptoProvider
{
    /// <summary>
    /// Ensure the JS crypto module is initialized.
    /// </summary>
    ValueTask EnsureInitializedAsync();

    /// <summary>
    /// Generate a new VAPID ECDSA P-256 keypair.
    /// Returns packed bytes: [publicKey(65) | privateKeyPkcs8(N)].
    /// The CryptoKey is automatically cached in JS for signing.
    /// </summary>
    Task<byte[]> GenerateKeyPairAsync();

    /// <summary>
    /// Import a VAPID keypair from stored components and cache for signing.
    /// </summary>
    /// <param name="publicKeyBase64">Base64-encoded 65-byte uncompressed public key.</param>
    /// <param name="pkcs8PrivateKey">
    /// PKCS8-encoded private key bytes. Crosses to JS as a binary
    /// <c>MemoryView</c> — no immutable Base64 string carrying the secret on
    /// either heap (P21). Caller owns the buffer and is responsible for
    /// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>
    /// at end-of-life.
    /// </param>
    /// <returns>True if import succeeded.</returns>
    Task<bool> ImportKeyPairAsync(string publicKeyBase64, ReadOnlyMemory<byte> pkcs8PrivateKey);

    /// <summary>
    /// Whether a VAPID key is currently loaded in the JS cache.
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Clear the cached VAPID key from memory.
    /// </summary>
    void ClearKey();

    /// <summary>
    /// Send an encrypted push notification using the cached VAPID key.
    /// </summary>
    /// <param name="endpoint">Push service endpoint URL</param>
    /// <param name="p256dhBase64">Subscriber's P-256 ECDH public key (Base64)</param>
    /// <param name="authBase64">Subscriber's auth secret (Base64)</param>
    /// <param name="payloadBase64">Plaintext payload to encrypt (Base64)</param>
    /// <param name="subject">VAPID subject (e.g. "mailto:user@example.com")</param>
    /// <param name="proxyUrl">Push proxy URL on the relay (CORS bypass)</param>
    /// <param name="apiKey">API key for proxy authentication</param>
    /// <param name="ttl">Time-to-live in seconds</param>
    /// <returns>Structured push outcome — inspect <see cref="PushSendResult.IsVapidKeyStale"/>
    /// or <see cref="PushSendResult.Gone"/> to drive recovery.</returns>
    Task<PushSendResult> SendPushNotificationAsync(
        string endpoint, string p256dhBase64, string authBase64,
        string payloadBase64, string subject, string proxyUrl, string apiKey, int ttl);
}
