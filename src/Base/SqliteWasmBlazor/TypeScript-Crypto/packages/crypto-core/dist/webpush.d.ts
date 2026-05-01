/**
 * Encrypted push message ready to send.
 */
export interface WebPushMessage {
    /** The encrypted payload (aes128gcm content encoding) */
    payload: Uint8Array;
    /** Headers to include in the push request */
    headers: Record<string, string>;
}
/**
 * Result of sending a push notification.
 */
export interface WebPushResult {
    success: boolean;
    status: number;
    endpoint: string;
    /** True if subscription is expired (410 Gone) or invalid (404) */
    gone: boolean;
    /**
     * Push service's `reason` field if it returned a structured error body
     * (e.g. Apple's "VapidPkHashMismatch", "BadJwtToken"). Null otherwise.
     */
    reason: string | null;
    /** Raw response body from the push service (best-effort, may be empty). */
    responseBody: string | null;
}
/**
 * Subscriber's push subscription keys.
 */
export interface PushSubscriptionKeys {
    /** ECDH P-256 public key (65 bytes uncompressed, or base64url-encoded) */
    p256dh: Uint8Array;
    /** Auth secret (16 bytes, or base64url-encoded) */
    auth: Uint8Array;
}
/**
 * Encrypt a push message payload per RFC 8291.
 *
 * @param plaintext - The message to encrypt (must fit in single record, ~3993 bytes max)
 * @param subscriptionKeys - Subscriber's p256dh and auth keys
 * @returns Encrypted payload with aes128gcm content encoding header
 */
export declare function encryptPushPayload(plaintext: Uint8Array, subscriptionKeys: PushSubscriptionKeys): Promise<Uint8Array>;
/**
 * Send an encrypted push notification via a server-side proxy.
 * All crypto (VAPID JWT + RFC 8291) is done client-side.
 * The proxy just forwards the prepared request to bypass CORS.
 *
 * @param endpoint - Push service endpoint URL
 * @param subscriptionKeys - Subscriber's p256dh and auth keys
 * @param payload - Plaintext payload to encrypt and send (JSON notification data)
 * @param vapidPrivateKey - VAPID ECDSA P-256 private key (CryptoKey)
 * @param vapidPublicKey - VAPID raw public key (65 bytes)
 * @param subject - VAPID subject (e.g. "mailto:user@example.com")
 * @param proxyUrl - URL of the push proxy endpoint (e.g. "https://relay.example.com/push")
 * @param apiKey - API key for push proxy authentication
 * @param ttl - Time-to-live in seconds (default 86400 = 24h)
 */
export declare function sendPushNotification(endpoint: string, subscriptionKeys: PushSubscriptionKeys, payload: Uint8Array, vapidPrivateKey: CryptoKey, vapidPublicKey: Uint8Array, subject: string, proxyUrl: string, apiKey: string, ttl?: number): Promise<WebPushResult>;
//# sourceMappingURL=webpush.d.ts.map