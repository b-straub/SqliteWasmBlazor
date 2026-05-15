// @sqlitewasmblazor/crypto-core — WebPush RFC 8291 encryption + push sending
// Implements "Encrypted Content-Encoding for HTTP" (RFC 8188) with
// "Message Encryption for Web Push" (RFC 8291) using Web Crypto API.

import { toBuffer, concatBytes, generateRandomBytes, bytesToBase64 } from './utils.js';
import { createVapidAuthHeader } from './vapid.js';

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

// RFC 8291 constants
const CONTENT_ENCODING = 'aes128gcm';
const KEY_LENGTH_AES128 = 16;
const NONCE_LENGTH = 12;
const TAG_LENGTH = 16;
const SALT_LENGTH = 16;
// Single-record limit: 4096 bytes per RFC 8291 (payload + 1 byte padding delimiter + 16 byte tag)
const RECORD_SIZE = 4096;
const PADDING_DELIMITER = 2; // 0x02 for final record

/**
 * Encrypt a push message payload per RFC 8291.
 *
 * @param plaintext - The message to encrypt (must fit in single record, ~3993 bytes max)
 * @param subscriptionKeys - Subscriber's p256dh and auth keys
 * @returns Encrypted payload with aes128gcm content encoding header
 */
export async function encryptPushPayload(
    plaintext: Uint8Array,
    subscriptionKeys: PushSubscriptionKeys
): Promise<Uint8Array> {
    // Validate plaintext fits in a single record
    // Max plaintext = RECORD_SIZE - TAG_LENGTH - 1 (padding delimiter) = 4079
    const maxPlaintext = RECORD_SIZE - TAG_LENGTH - 1;
    if (plaintext.length > maxPlaintext) {
        throw new Error(`Payload too large: ${plaintext.length} bytes exceeds ${maxPlaintext} byte limit`);
    }

    // Generate ephemeral ECDH key pair
    const localKeyPair = await crypto.subtle.generateKey(
        { name: 'ECDH', namedCurve: 'P-256' },
        true,
        ['deriveBits']
    );

    // Export local public key (65 bytes, uncompressed)
    const localPublicKey = new Uint8Array(
        await crypto.subtle.exportKey('raw', localKeyPair.publicKey)
    );

    // Import subscriber's p256dh public key
    const subscriberKey = await crypto.subtle.importKey(
        'raw',
        toBuffer(subscriptionKeys.p256dh),
        { name: 'ECDH', namedCurve: 'P-256' },
        false,
        []
    );

    // ECDH key agreement
    const sharedSecret = new Uint8Array(
        await crypto.subtle.deriveBits(
            { name: 'ECDH', public: subscriberKey },
            localKeyPair.privateKey,
            256
        )
    );

    // Generate random salt
    const salt = generateRandomBytes(SALT_LENGTH);

    // Derive encryption key and nonce per RFC 8291 Section 3.4
    const { contentKey, nonce } = await deriveKeyAndNonce(
        sharedSecret,
        salt,
        subscriptionKeys.auth,
        localPublicKey,
        subscriptionKeys.p256dh
    );

    // Pad plaintext: content + delimiter (0x02 for final record)
    const padded = concatBytes(plaintext, new Uint8Array([PADDING_DELIMITER]));

    // Encrypt with AES-128-GCM
    const ciphertext = new Uint8Array(
        await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv: toBuffer(nonce), tagLength: 128 },
            contentKey,
            toBuffer(padded)
        )
    );

    // Build aes128gcm encoded payload:
    // salt (16) || record_size (4, big-endian) || keyid_len (1) || keyid (65) || ciphertext+tag
    const recordSizeBytes = new Uint8Array(4);
    new DataView(recordSizeBytes.buffer).setUint32(0, RECORD_SIZE, false);

    const keyIdLen = new Uint8Array([localPublicKey.length]);

    return concatBytes(salt, recordSizeBytes, keyIdLen, localPublicKey, ciphertext);
}

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
export async function sendPushNotification(
    endpoint: string,
    subscriptionKeys: PushSubscriptionKeys,
    payload: Uint8Array,
    vapidPrivateKey: CryptoKey,
    vapidPublicKey: Uint8Array,
    subject: string,
    proxyUrl: string,
    apiKey: string,
    ttl: number = 86400
): Promise<WebPushResult> {
    // Encrypt payload client-side
    const encryptedPayload = await encryptPushPayload(payload, subscriptionKeys);

    // Build VAPID auth header client-side
    const audience = new URL(endpoint).origin;
    const authorization = await createVapidAuthHeader(
        audience, subject, vapidPrivateKey, vapidPublicKey
    );

    // Send via proxy — proxy forwards to push service endpoint (bypasses CORS)
    const response = await fetch(proxyUrl, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'X-Api-Key': apiKey,
        },
        body: JSON.stringify({
            endpoint,
            authorization,
            payload: bytesToBase64(encryptedPayload),
            ttl,
        }),
    });

    const result = await response.json().catch(() => ({} as { pushStatus?: number; response?: string }));
    const status = result.pushStatus ?? response.status;
    const gone = status === 404 || status === 410;
    const responseBody = typeof result.response === 'string' ? result.response : null;
    const reason = extractReason(responseBody);

    return {
        success: status >= 200 && status < 300,
        status,
        endpoint,
        gone,
        reason,
        responseBody,
    };
}

/**
 * Push services (Apple, FCM, Mozilla) return a structured JSON body on errors —
 * Apple's `{"reason":"VapidPkHashMismatch"}` is the canonical case. Returns the
 * `reason` string if parseable, null otherwise.
 */
function extractReason(body: string | null): string | null {
    if (body === null || body.length === 0) {
        return null;
    }
    try {
        const parsed = JSON.parse(body) as { reason?: unknown };
        return typeof parsed.reason === 'string' ? parsed.reason : null;
    } catch {
        return null;
    }
}

// ============================================================
// RFC 8291 KEY DERIVATION (Section 3.4)
// ============================================================

/**
 * Derive content encryption key (CEK) and nonce per RFC 8291.
 *
 * IKM = HKDF-SHA256(auth_secret, ecdh_shared_secret, "WebPush: info" || 0x00 || subscriber_pub || sender_pub || 0x01)
 * CEK = HKDF-SHA256(salt, IKM, "Content-Encoding: aes128gcm" || 0x00 || 0x01, 16)
 * nonce = HKDF-SHA256(salt, IKM, "Content-Encoding: nonce" || 0x00 || 0x01, 12)
 */
async function deriveKeyAndNonce(
    sharedSecret: Uint8Array,
    salt: Uint8Array,
    authSecret: Uint8Array,
    senderPublicKey: Uint8Array,
    subscriberPublicKey: Uint8Array
): Promise<{ contentKey: CryptoKey; nonce: Uint8Array }> {
    const encoder = new TextEncoder();

    // Step 1: Derive IKM from ECDH shared secret + auth secret
    // info = "WebPush: info" || 0x00 || subscriber_pub || sender_pub
    const infoIkm = concatBytes(
        encoder.encode('WebPush: info\0'),
        subscriberPublicKey,
        senderPublicKey
    );

    const ikmBits = await hkdfDeriveBits(authSecret, sharedSecret, infoIkm, 256);

    // Step 2: Derive CEK from IKM + salt
    // info = "Content-Encoding: aes128gcm" || 0x00
    const infoCek = encoder.encode('Content-Encoding: aes128gcm\0');
    const cekBits = await hkdfDeriveBits(salt, ikmBits, infoCek, KEY_LENGTH_AES128 * 8);

    const contentKey = await crypto.subtle.importKey(
        'raw',
        toBuffer(cekBits),
        { name: 'AES-GCM' },
        false,
        ['encrypt']
    );

    // Step 3: Derive nonce from IKM + salt
    // info = "Content-Encoding: nonce" || 0x00
    const infoNonce = encoder.encode('Content-Encoding: nonce\0');
    const nonce = await hkdfDeriveBits(salt, ikmBits, infoNonce, NONCE_LENGTH * 8);

    return { contentKey, nonce };
}

/**
 * HKDF-SHA256 using Web Crypto API.
 */
async function hkdfDeriveBits(
    salt: Uint8Array,
    ikm: Uint8Array,
    info: Uint8Array,
    bits: number
): Promise<Uint8Array> {
    const baseKey = await crypto.subtle.importKey(
        'raw',
        toBuffer(ikm),
        { name: 'HKDF' },
        false,
        ['deriveBits']
    );

    const derived = await crypto.subtle.deriveBits(
        {
            name: 'HKDF',
            hash: 'SHA-256',
            salt: toBuffer(salt),
            info: toBuffer(info),
        },
        baseKey,
        bits
    );

    return new Uint8Array(derived);
}
