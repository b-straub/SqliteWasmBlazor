/**
 * SqliteWasmBlazor.Crypto Binary Bridge — thin layer between C# JSImport and @sqlitewasmblazor/crypto-core.
 *
 * Non-cached operations delegate to crypto-core and pack results as Uint8Array.
 * Cached key operations use SubtleCrypto CryptoKey objects directly (non-extractable).
 *
 * Binary convention: fixed-size headers, see packed layout comments on each function.
 */

import {
    // Primitives
    generateX25519KeyPair as coreGenerateX25519KeyPair,
    getX25519PublicKey as coreGetX25519PublicKey,
    generateEd25519KeyPair as coreGenerateEd25519KeyPair,
    getEd25519PublicKey as coreGetEd25519PublicKey,
    ed25519Sign as coreEd25519Sign,
    ed25519Verify as coreEd25519Verify,
    encryptAesGcm as coreEncryptAesGcm,
    decryptAesGcm as coreDecryptAesGcm,
    encryptAsymmetric as coreEncryptAsymmetric,
    decryptAsymmetric as coreDecryptAsymmetric,
    // Key derivation
    deriveX25519KeyPairFromSeed,
    deriveEd25519KeyPairFromSeed,
    deriveDualKeyPair as coreDeriveDualKeyPair,
    deriveHkdfKey as coreHkdfKey,
    deriveWrappingKey as coreDeriveWrappingKey,
    // VAPID + WebPush
    generateVapidKeyPair as coreGenerateVapidKeyPair,
    importVapidPrivateKey as coreImportVapidPrivateKey,
    encryptPushPayload as coreEncryptPushPayload,
    sendPushNotification as coreSendPushNotification,
    createVapidAuthHeader as coreCreateVapidAuthHeader,
    // Utils
    clearBytes,
    concatBytes,
    toBuffer,
    wrapSeedInPkcs8,
    base64UrlToBase64,
    base64ToBytes,
    bytesToBase64,
    generateRandomBytes as coreGenerateRandomBytes,
    // Constants
    NONCE_LENGTH_AES,
} from '@sqlitewasmblazor/crypto-core';

import type { PushSubscriptionKeys } from '@sqlitewasmblazor/crypto-core';

/**
 * .NET MemoryView marshalling produces a runtime <c>Span</c> object — NOT a
 * Uint8Array. Noble's <c>isBytes</c> check (<c>instanceof Uint8Array</c>) fails
 * against it. Bridge functions that accept a MemoryView call <c>.slice()</c> to
 * lift the bytes into a real Uint8Array before passing them downstream.
 * Mirror of the same shape used by SqliteWasmBlazor/TypeScript/bridge/worker-bridge.ts.
 */
interface IMemoryView {
    slice(): Uint8Array;
    slice(start: number): Uint8Array;
    slice(start: number, end: number): Uint8Array;
}

// ============================================================
// KEY CACHE (Keys stored in JS, C# only references by keyId)
// ============================================================

interface CachedKeySet {
    x25519Private: Uint8Array;
    x25519Public: Uint8Array;
    ed25519SigningKey: CryptoKey;    // non-extractable
    ed25519Public: Uint8Array;
    aesEncryptKey: CryptoKey;       // non-extractable
    aesDecryptKey: CryptoKey;       // non-extractable
    expiresAt: number | null;
    expirationTimer: number | null;
}

const keyCache = new Map<string, CachedKeySet>();

function isExpired(keys: CachedKeySet): boolean {
    return keys.expiresAt !== null && Date.now() >= keys.expiresAt;
}

function getCachedKeys(keyId: string): CachedKeySet | null {
    const keys = keyCache.get(keyId);
    if (!keys || isExpired(keys)) {
        if (keys) {
            removeKeys(keyId);
        }
        return null;
    }
    return keys;
}

// ============================================================
// KEY CACHE MANAGEMENT
// ============================================================

/**
 * Store and derive all keys from PRF seed.
 * Returns: [x25519Pub(32) | ed25519Pub(32)] = 64 bytes
 */
export async function storeKeys(keyId: string, seed: Uint8Array, ttlMs: number | null): Promise<Uint8Array> {
    try {
        // Derive X25519 keypair via crypto-core
        const x25519Kp = deriveX25519KeyPairFromSeed(seed);
        const x25519Private = x25519Kp.privateKey;
        const x25519Public = x25519Kp.publicKey;

        // Derive Ed25519 seed and import as non-extractable CryptoKey
        const ed25519Seed = coreHkdfKey(seed, 'ed25519-key', 32);
        const pkcs8Key = wrapSeedInPkcs8(ed25519Seed);
        const ed25519SigningKey = await crypto.subtle.importKey(
            "pkcs8", toBuffer(pkcs8Key), { name: "Ed25519" }, false, ["sign"]
        );

        // Get public key via temporary extractable import
        const tempKey = await crypto.subtle.importKey(
            "pkcs8", toBuffer(pkcs8Key), { name: "Ed25519" }, true, ["sign"]
        );
        const jwk = await crypto.subtle.exportKey("jwk", tempKey);
        const ed25519Public = base64ToBytes(base64UrlToBase64(jwk.x!));

        clearBytes(ed25519Seed);
        clearBytes(pkcs8Key);

        // Derive symmetric key as non-extractable AES CryptoKey
        const symmetricKey = coreHkdfKey(seed, 'symmetric-key', 32);
        const aesEncryptKey = await crypto.subtle.importKey(
            'raw', toBuffer(symmetricKey), { name: 'AES-GCM' }, false, ['encrypt']
        );
        const aesDecryptKey = await crypto.subtle.importKey(
            'raw', toBuffer(symmetricKey), { name: 'AES-GCM' }, false, ['decrypt']
        );
        clearBytes(symmetricKey);

        // Remove existing entry
        removeKeys(keyId);

        const expiresAt = ttlMs !== null ? Date.now() + ttlMs : null;
        let expirationTimer: number | null = null;
        if (ttlMs !== null) {
            expirationTimer = window.setTimeout(() => { removeKeys(keyId); }, ttlMs);
        }

        keyCache.set(keyId, {
            x25519Private, x25519Public, ed25519SigningKey, ed25519Public,
            aesEncryptKey, aesDecryptKey, expiresAt, expirationTimer
        });

        return concatBytes(x25519Public, ed25519Public);
    } catch {
        return new Uint8Array(0);
    }
}

/**
 * Get public keys for a cached key set.
 * Returns: [x25519Pub(32) | ed25519Pub(32)] = 64 bytes, or empty on error.
 */
export function getPublicKeys(keyId: string): Uint8Array {
    const keys = keyCache.get(keyId);
    if (!keys || isExpired(keys)) {
        return new Uint8Array(0);
    }
    return concatBytes(keys.x25519Public, keys.ed25519Public);
}

export function hasKey(keyId: string): boolean {
    const keys = keyCache.get(keyId);
    return keys !== undefined && !isExpired(keys);
}

export function removeKeys(keyId: string): void {
    const keys = keyCache.get(keyId);
    if (keys) {
        if (keys.expirationTimer !== null) {
            clearTimeout(keys.expirationTimer);
        }
        clearBytes(keys.x25519Private);
        clearBytes(keys.x25519Public);
        clearBytes(keys.ed25519Public);
        keyCache.delete(keyId);
    }
}

export function clearAllKeys(): void {
    for (const keyId of keyCache.keys()) {
        removeKeys(keyId);
    }
}

// ============================================================
// CACHED KEY OPERATIONS (SubtleCrypto CryptoKey, non-extractable)
// ============================================================

/**
 * Sign with Ed25519 using cached non-extractable CryptoKey.
 * Returns: [signature(64)] or empty on error.
 */
export async function signWithCachedKey(keyId: string, message: Uint8Array): Promise<Uint8Array> {
    const keys = getCachedKeys(keyId);
    if (!keys) {
        return new Uint8Array(0);
    }
    try {
        const sig = await crypto.subtle.sign({ name: "Ed25519" }, keys.ed25519SigningKey, toBuffer(message));
        return new Uint8Array(sig);
    } catch {
        return new Uint8Array(0);
    }
}

/**
 * Encrypt symmetric with cached AES CryptoKey.
 * Returns: [nonce(12) | ciphertext(N)] or empty on error.
 */
export async function encryptSymmetricCachedAesGcm(
    keyId: string, plaintext: Uint8Array, aad: string | null = null
): Promise<Uint8Array> {
    const keys = getCachedKeys(keyId);
    if (!keys) {
        return new Uint8Array(0);
    }
    try {
        const nonce = coreGenerateRandomBytes(NONCE_LENGTH_AES);
        const params: AesGcmParams = { name: 'AES-GCM', iv: toBuffer(nonce) };
        if (aad !== null) {
            params.additionalData = new TextEncoder().encode(aad);
        }
        const ciphertext = await crypto.subtle.encrypt(params, keys.aesEncryptKey, toBuffer(plaintext));
        return concatBytes(nonce, new Uint8Array(ciphertext));
    } catch {
        return new Uint8Array(0);
    }
}

/**
 * Decrypt symmetric with cached AES CryptoKey.
 * Takes separate nonce and ciphertext. Returns plaintext or empty on error.
 */
export async function decryptSymmetricCachedAesGcm(
    keyId: string, ciphertext: Uint8Array, nonce: Uint8Array, aad: string | null = null
): Promise<Uint8Array> {
    const keys = getCachedKeys(keyId);
    if (!keys) {
        return new Uint8Array(0);
    }
    try {
        const params: AesGcmParams = { name: 'AES-GCM', iv: toBuffer(nonce) };
        if (aad !== null) {
            params.additionalData = new TextEncoder().encode(aad);
        }
        const plaintext = await crypto.subtle.decrypt(params, keys.aesDecryptKey, toBuffer(ciphertext));
        return new Uint8Array(plaintext);
    } catch {
        return new Uint8Array(0);
    }
}

/**
 * Decrypt asymmetric (ECIES) with cached X25519 private key.
 * Returns plaintext or empty on error.
 */
export async function decryptAsymmetricCachedAesGcm(
    keyId: string, ephemeralPublicKey: Uint8Array, ciphertext: Uint8Array, nonce: Uint8Array
): Promise<Uint8Array> {
    const keys = getCachedKeys(keyId);
    if (!keys) {
        return new Uint8Array(0);
    }
    try {
        const result = await coreDecryptAsymmetric(
            { ephemeralPublicKey, ciphertext, nonce },
            keys.x25519Private
        );
        return result;
    } catch {
        return new Uint8Array(0);
    }
}

// ============================================================
// NON-CACHED OPERATIONS (delegate to crypto-core, pack as Uint8Array)
// ============================================================

/** Returns: [privKey(32) | pubKey(32)] */
export function generateX25519KeyPair(): Uint8Array {
    const kp = coreGenerateX25519KeyPair();
    const result = concatBytes(kp.privateKey, kp.publicKey);
    clearBytes(kp.privateKey);
    return result;
}

/** Returns: pubKey(32) */
export function getX25519PublicKey(privateKey: Uint8Array): Uint8Array {
    return coreGetX25519PublicKey(privateKey);
}

/** Returns: [privKey(32) | pubKey(32)] */
export function deriveX25519KeyPair(seed: Uint8Array): Uint8Array {
    const kp = deriveX25519KeyPairFromSeed(seed);
    const result = concatBytes(kp.privateKey, kp.publicKey);
    clearBytes(kp.privateKey);
    return result;
}

/** Returns: [privKey(32) | pubKey(32)] */
export function generateEd25519KeyPair(): Uint8Array {
    const kp = coreGenerateEd25519KeyPair();
    const result = concatBytes(kp.privateKey, kp.publicKey);
    clearBytes(kp.privateKey);
    return result;
}

/** Returns: pubKey(32) */
export function getEd25519PublicKey(privateKey: Uint8Array): Uint8Array {
    return coreGetEd25519PublicKey(privateKey);
}

/** Returns: [privKey(32) | pubKey(32)] */
export function deriveEd25519KeyPair(seed: Uint8Array): Uint8Array {
    const kp = deriveEd25519KeyPairFromSeed(seed);
    const result = concatBytes(kp.privateKey, kp.publicKey);
    clearBytes(kp.privateKey);
    return result;
}

/** Returns: signature(64) */
export function ed25519Sign(message: Uint8Array, privateKey: Uint8Array): Uint8Array {
    return coreEd25519Sign(message, privateKey);
}

/** Returns: boolean */
export function ed25519Verify(signature: Uint8Array, message: Uint8Array, publicKey: Uint8Array): boolean {
    return coreEd25519Verify(signature, message, publicKey);
}

/** Returns: [x25519Priv(32) | x25519Pub(32) | ed25519Priv(32) | ed25519Pub(32)] = 128 bytes */
export function deriveDualKeyPair(seed: Uint8Array): Uint8Array {
    const dual = coreDeriveDualKeyPair(seed);
    const result = concatBytes(dual.x25519PrivateKey, dual.x25519PublicKey, dual.ed25519PrivateKey, dual.ed25519PublicKey);
    clearBytes(dual.x25519PrivateKey);
    clearBytes(dual.ed25519PrivateKey);
    return result;
}

/** Returns: [nonce(12) | ciphertext(N)] */
export async function encryptAesGcm(plaintext: Uint8Array, key: Uint8Array, aad: string | null = null): Promise<Uint8Array> {
    const aadBytes = aad !== null ? new TextEncoder().encode(aad) : undefined;
    const result = await coreEncryptAesGcm(plaintext, key, aadBytes);
    return concatBytes(result.nonce, result.ciphertext);
}

/** Returns: plaintext bytes */
export async function decryptAesGcm(ciphertext: Uint8Array, nonce: Uint8Array, key: Uint8Array, aad: string | null = null): Promise<Uint8Array> {
    const aadBytes = aad !== null ? new TextEncoder().encode(aad) : undefined;
    return coreDecryptAesGcm({ ciphertext, nonce }, key, aadBytes);
}

/** Returns: [ephPubKey(32) | nonce(12) | ciphertext(N)] */
export async function encryptAsymmetricAesGcm(plaintext: Uint8Array, recipientPublicKey: Uint8Array): Promise<Uint8Array> {
    const result = await coreEncryptAsymmetric(plaintext, recipientPublicKey);
    return concatBytes(result.ephemeralPublicKey, result.nonce, result.ciphertext);
}

/** Returns: plaintext bytes */
export async function decryptAsymmetricAesGcm(
    ephemeralPublicKey: Uint8Array, ciphertext: Uint8Array, nonce: Uint8Array, privateKey: Uint8Array
): Promise<Uint8Array> {
    return coreDecryptAsymmetric({ ephemeralPublicKey, ciphertext, nonce }, privateKey);
}

/** Returns: derivedKey(32) */
export function deriveHkdfKey(seed: Uint8Array, domain: string): Uint8Array {
    return coreHkdfKey(seed, domain, 32);
}

/** Returns: wrappingKey(32) */
export function deriveWrappingKey(ownPrivateKey: Uint8Array, recipientPublicKey: Uint8Array, context: string): Uint8Array {
    return coreDeriveWrappingKey(ownPrivateKey, recipientPublicKey, context);
}

/** Returns: randomBytes(N) */
export function generateRandomBytes(length: number): Uint8Array {
    return coreGenerateRandomBytes(length);
}

/** Check if all crypto features are available */
export function isSupported(): boolean {
    try {
        const testKey = coreGenerateRandomBytes(32);
        coreGetX25519PublicKey(testKey);
        coreGetEd25519PublicKey(testKey);
        return typeof crypto !== 'undefined' && typeof crypto.subtle !== 'undefined';
    } catch {
        return false;
    }
}

// ============================================================
// BASE64 BRIDGE FUNCTIONS (for JSImport — packed binary as Base64 strings)
// These are the exports consumed by C# via NobleInterop.cs.
// ============================================================

/** Base64([privKey(32)|pubKey(32)]) */
export function generateX25519KeyPairB64(): string { return bytesToBase64(generateX25519KeyPair()); }
export function getX25519PublicKeyB64(privB64: string): string { return bytesToBase64(getX25519PublicKey(base64ToBytes(privB64))); }
export function deriveX25519KeyPairB64(seedB64: string): string { return bytesToBase64(deriveX25519KeyPair(base64ToBytes(seedB64))); }

export function generateEd25519KeyPairB64(): string { return bytesToBase64(generateEd25519KeyPair()); }
export function getEd25519PublicKeyB64(privB64: string): string { return bytesToBase64(getEd25519PublicKey(base64ToBytes(privB64))); }
export function deriveEd25519KeyPairB64(seedB64: string): string { return bytesToBase64(deriveEd25519KeyPair(base64ToBytes(seedB64))); }

/**
 * Base64(signature(64)). privKey crosses as a .NET MemoryView — a Span runtime
 * object whose <c>instanceof Uint8Array</c> is false, so noble rejects it. Slice
 * into a real Uint8Array, then zeroize that copy in finally.
 */
export function ed25519SignB64(msgB64: string, privKey: IMemoryView): string {
    const priv = privKey.slice();
    try {
        return bytesToBase64(ed25519Sign(base64ToBytes(msgB64), priv));
    } finally {
        clearBytes(priv);
    }
}

export function ed25519VerifyB64(sigB64: string, msgB64: string, pubB64: string): boolean {
    return ed25519Verify(base64ToBytes(sigB64), base64ToBytes(msgB64), base64ToBytes(pubB64));
}

/** Base64([x25519Priv(32)|x25519Pub(32)|ed25519Priv(32)|ed25519Pub(32)]) */
export function deriveDualKeyPairB64(seedB64: string): string { return bytesToBase64(deriveDualKeyPair(base64ToBytes(seedB64))); }

/**
 * Base64([nonce(12)|ciphertext]). Both plaintext and key cross as .NET
 * MemoryViews; slice into real Uint8Arrays and zeroize the secret slice.
 * The plaintext slice is NOT zeroed since callers may pass user-text bytes
 * whose lifetime they manage.
 */
export async function encryptAesGcmB64(plaintext: IMemoryView, key: IMemoryView, aad: string | null = null): Promise<string> {
    const ptCopy = plaintext.slice();
    const keyCopy = key.slice();
    try {
        return bytesToBase64(await encryptAesGcm(ptCopy, keyCopy, aad));
    } finally {
        clearBytes(keyCopy);
    }
}

/** Base64(plaintext). key crosses as a .NET MemoryView; slice + zeroize. */
export async function decryptAesGcmB64(ctB64: string, nonceB64: string, key: IMemoryView, aad: string | null = null): Promise<string> {
    const keyCopy = key.slice();
    try {
        return bytesToBase64(await decryptAesGcm(base64ToBytes(ctB64), base64ToBytes(nonceB64), keyCopy, aad));
    } finally {
        clearBytes(keyCopy);
    }
}

/** Base64([ephPubKey(32)|nonce(12)|ciphertext]) */
export async function encryptAsymmetricB64(ptB64: string, recipPubB64: string): Promise<string> {
    return bytesToBase64(await encryptAsymmetricAesGcm(base64ToBytes(ptB64), base64ToBytes(recipPubB64)));
}

/** Base64(plaintext). privKey crosses as a .NET MemoryView; slice + zeroize. */
export async function decryptAsymmetricB64(ephPubB64: string, ctB64: string, nonceB64: string, privKey: IMemoryView): Promise<string> {
    const priv = privKey.slice();
    try {
        return bytesToBase64(await decryptAsymmetricAesGcm(base64ToBytes(ephPubB64), base64ToBytes(ctB64), base64ToBytes(nonceB64), priv));
    } finally {
        clearBytes(priv);
    }
}

export function deriveHkdfKeyB64(seedB64: string, domain: string): string { return bytesToBase64(deriveHkdfKey(base64ToBytes(seedB64), domain)); }
/** ownPrivateKey crosses as a .NET MemoryView; slice + zeroize. */
export function deriveWrappingKeyB64(ownPrivateKey: IMemoryView, recipPubB64: string, ctx: string): string {
    const priv = ownPrivateKey.slice();
    try {
        return bytesToBase64(deriveWrappingKey(priv, base64ToBytes(recipPubB64), ctx));
    } finally {
        clearBytes(priv);
    }
}
export function generateRandomBytesB64(length: number): string { return bytesToBase64(generateRandomBytes(length)); }

/** Base64([x25519Pub(32)|ed25519Pub(32)]) */
export async function storeKeysB64(keyId: string, seedB64: string, ttlMs: number | null): Promise<string> {
    return bytesToBase64(await storeKeys(keyId, base64ToBytes(seedB64), ttlMs));
}
export function getPublicKeysB64(keyId: string): string { return bytesToBase64(getPublicKeys(keyId)); }

/** Base64(signature(64)) */
export async function signWithCachedKeyB64(keyId: string, msgB64: string): Promise<string> {
    return bytesToBase64(await signWithCachedKey(keyId, base64ToBytes(msgB64)));
}

/** Base64([nonce(12)|ciphertext]) */
export async function encryptSymmetricCachedB64(keyId: string, ptB64: string, aad: string | null = null): Promise<string> {
    return bytesToBase64(await encryptSymmetricCachedAesGcm(keyId, base64ToBytes(ptB64), aad));
}

/** Base64(plaintext) */
export async function decryptSymmetricCachedB64(keyId: string, ctB64: string, nonceB64: string, aad: string | null = null): Promise<string> {
    return bytesToBase64(await decryptSymmetricCachedAesGcm(keyId, base64ToBytes(ctB64), base64ToBytes(nonceB64), aad));
}

/** Base64(plaintext) */
export async function decryptAsymmetricCachedB64(keyId: string, ephPubB64: string, ctB64: string, nonceB64: string): Promise<string> {
    return bytesToBase64(await decryptAsymmetricCachedAesGcm(keyId, base64ToBytes(ephPubB64), base64ToBytes(ctB64), base64ToBytes(nonceB64)));
}

// ============================================================
// VAPID + WEBPUSH
// ============================================================

// In-memory VAPID CryptoKey cache (private key is non-extractable after import)
let vapidCryptoKey: CryptoKey | null = null;
let vapidPublicKeyBytes: Uint8Array | null = null;

/**
 * Generate a new VAPID ECDSA P-256 keypair.
 * Returns: [publicKey(65) | privateKeyPkcs8(N)]
 * The private key is PKCS8-encoded for encrypted storage.
 */
export async function generateVapidKeyPair(): Promise<Uint8Array> {
    const kp = await coreGenerateVapidKeyPair();
    const result = concatBytes(kp.publicKey, kp.privateKeyPkcs8);
    // Cache the CryptoKey for immediate use
    vapidCryptoKey = kp.cryptoKey;
    vapidPublicKeyBytes = kp.publicKey;
    return result;
}

/**
 * Import a VAPID private key from PKCS8 bytes and cache the CryptoKey.
 * Also requires the public key for cache. Returns true on success.
 */
export async function importVapidKeyPair(publicKey: Uint8Array, pkcs8PrivateKey: Uint8Array): Promise<boolean> {
    try {
        vapidCryptoKey = await coreImportVapidPrivateKey(pkcs8PrivateKey);
        vapidPublicKeyBytes = publicKey;
        return true;
    } catch (e) {
        console.error('importVapidKeyPair failed:', e);
        return false;
    }
}

/**
 * Encrypt a push payload for a subscriber and send it.
 * Uses the cached VAPID key. Returns: status code (0 on error).
 */
export async function sendPushNotification(
    endpoint: string,
    p256dhB64: string,
    authB64: string,
    payload: Uint8Array,
    subject: string,
    proxyUrl: string,
    apiKey: string,
    ttl: number
): Promise<string> {
    if (vapidCryptoKey === null || vapidPublicKeyBytes === null) {
        console.error('sendPushNotification: VAPID key not loaded');
        return JSON.stringify({ success: false, status: 0, endpoint, gone: false, reason: null, responseBody: null });
    }
    try {
        // Subscription keys from browser are URL-safe Base64 — convert to standard
        const subscriptionKeys: PushSubscriptionKeys = {
            p256dh: base64ToBytes(base64UrlToBase64(p256dhB64)),
            auth: base64ToBytes(base64UrlToBase64(authB64)),
        };
        const result = await coreSendPushNotification(
            endpoint, subscriptionKeys, payload,
            vapidCryptoKey, vapidPublicKeyBytes, subject, proxyUrl, apiKey, ttl
        );
        if (!result.success) {
            const reasonSuffix = result.reason !== null ? ` reason=${result.reason}` : '';
            const goneSuffix = result.gone ? ' (subscription gone)' : '';
            console.error(`sendPushNotification: HTTP ${result.status} to ${result.endpoint}${goneSuffix}${reasonSuffix}`);
        }
        return JSON.stringify(result);
    } catch (e) {
        console.error('sendPushNotification failed:', e);
        return JSON.stringify({ success: false, status: 0, endpoint, gone: false, reason: null, responseBody: null });
    }
}

/**
 * Encrypt a push payload without sending (for testing/inspection).
 * Returns the encrypted aes128gcm payload or empty on error.
 */
export async function encryptPushPayload(
    plaintext: Uint8Array,
    p256dh: Uint8Array,
    auth: Uint8Array
): Promise<Uint8Array> {
    try {
        return await coreEncryptPushPayload(plaintext, { p256dh, auth });
    } catch {
        return new Uint8Array(0);
    }
}

/** Check if VAPID key is loaded */
export function hasVapidKey(): boolean {
    return vapidCryptoKey !== null && vapidPublicKeyBytes !== null;
}

/** Clear cached VAPID key */
export function clearVapidKey(): void {
    vapidCryptoKey = null;
    vapidPublicKeyBytes = null;
}

// ============================================================
// VAPID + WEBPUSH BASE64 BRIDGE
// ============================================================

/** Base64([publicKey(65) | privateKeyPkcs8(N)]) */
export async function generateVapidKeyPairB64(): Promise<string> {
    return bytesToBase64(await generateVapidKeyPair());
}

/** Import VAPID keypair from Base64 components, returns "true"/"false" */
export async function importVapidKeyPairB64(publicKeyB64: string, pkcs8PrivateKeyB64: string): Promise<boolean> {
    return importVapidKeyPair(base64ToBytes(publicKeyB64), base64ToBytes(pkcs8PrivateKeyB64));
}

/**
 * Send push via proxy. Returns JSON-stringified WebPushResult:
 * `{ success, status, endpoint, gone, reason, responseBody }`. C# parses with
 * PushSendResult json context.
 */
export async function sendPushNotificationB64(
    endpoint: string, p256dhB64: string, authB64: string,
    payloadB64: string, subject: string, proxyUrl: string, apiKey: string, ttl: number
): Promise<string> {
    return sendPushNotification(endpoint, p256dhB64, authB64, base64ToBytes(payloadB64), subject, proxyUrl, apiKey, ttl);
}

/** Base64(encrypted aes128gcm payload) */
export async function encryptPushPayloadB64(
    plaintextB64: string, p256dhB64: string, authB64: string
): Promise<string> {
    return bytesToBase64(await encryptPushPayload(base64ToBytes(plaintextB64), base64ToBytes(p256dhB64), base64ToBytes(authB64)));
}
