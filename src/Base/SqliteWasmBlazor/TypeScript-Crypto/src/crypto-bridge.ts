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
    ed25519Sign as coreEd25519Sign,
    ed25519Verify as coreEd25519Verify,
    encryptAesGcm as coreEncryptAesGcm,
    decryptAesGcm as coreDecryptAesGcm,
    encryptAsymmetric as coreEncryptAsymmetric,
    decryptAsymmetric as coreDecryptAsymmetric,
    // Key derivation
    deriveX25519KeyPairFromSeed,
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
} from '@sqlitewasmblazor/crypto-core';

import type { PushSubscriptionKeys } from '@sqlitewasmblazor/crypto-core';

/**
 * .NET MemoryView marshalling produces a runtime <c>Span</c> object — NOT a
 * Uint8Array. Downstream consumers (SubtleCrypto's <c>toBuffer</c>, @awasm/noble's
 * <c>isBytes</c> check) require a real Uint8Array. Bridge functions that accept
 * a MemoryView call <c>.slice()</c> to lift the bytes before passing them
 * downstream. Mirror of the same shape used by
 * SqliteWasmBlazor/TypeScript/bridge/worker-bridge.ts.
 */
interface IMemoryView {
    readonly length: number;
    slice(): Uint8Array;
    slice(start: number): Uint8Array;
    slice(start: number, end: number): Uint8Array;
    /**
     * Copy bytes from a source Uint8Array into this view, starting at the
     * given target offset (default 0). Mirrors <c>Uint8Array.prototype.set</c>;
     * writes propagate back to the pinned C# memory the view aliases.
     * Used by byte-out bridge functions (P21 — output via writable view
     * rather than Base64-string return).
     */
    set(source: Uint8Array, targetOffset?: number): void;
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
 *
 * Failure-path hygiene: every derived secret temporary (Ed25519 seed, PKCS8
 * wrapper, raw symmetric key) is wrapped in a scoped finally so a throw from
 * crypto.subtle.importKey/exportKey cannot leave secret bytes on the JS heap.
 * The X25519 private key is the only secret retained past the try block, and
 * only once it has been successfully installed in keyCache; otherwise the
 * outer finally zeroizes it.
 */
export async function storeKeys(keyId: string, seed: Uint8Array, ttlMs: number | null): Promise<Uint8Array> {
    let x25519Private: Uint8Array | null = null;
    let x25519Public: Uint8Array | null = null;
    let ed25519Public: Uint8Array | null = null;
    let installed = false;
    try {
        // Derive X25519 keypair via crypto-core
        const x25519Kp = await deriveX25519KeyPairFromSeed(seed);
        x25519Private = x25519Kp.privateKey;
        x25519Public = x25519Kp.publicKey;

        // Derive Ed25519 seed and import as non-extractable CryptoKey
        const ed25519Seed = coreHkdfKey(seed, 'ed25519-key', 32);
        let ed25519SigningKey: CryptoKey;
        try {
            const pkcs8Key = wrapSeedInPkcs8(ed25519Seed, 'Ed25519');
            try {
                ed25519SigningKey = await crypto.subtle.importKey(
                    "pkcs8", toBuffer(pkcs8Key), { name: "Ed25519" }, false, ["sign"]
                );

                // Get public key via temporary extractable import
                const tempKey = await crypto.subtle.importKey(
                    "pkcs8", toBuffer(pkcs8Key), { name: "Ed25519" }, true, ["sign"]
                );
                const jwk = await crypto.subtle.exportKey("jwk", tempKey);
                ed25519Public = base64ToBytes(base64UrlToBase64(jwk.x!));
            } finally {
                clearBytes(pkcs8Key);
            }
        } finally {
            clearBytes(ed25519Seed);
        }

        // Derive symmetric key as non-extractable AES CryptoKey
        const symmetricKey = coreHkdfKey(seed, 'symmetric-key', 32);
        let aesEncryptKey: CryptoKey;
        let aesDecryptKey: CryptoKey;
        try {
            aesEncryptKey = await crypto.subtle.importKey(
                'raw', toBuffer(symmetricKey), { name: 'AES-GCM' }, false, ['encrypt']
            );
            aesDecryptKey = await crypto.subtle.importKey(
                'raw', toBuffer(symmetricKey), { name: 'AES-GCM' }, false, ['decrypt']
            );
        } finally {
            clearBytes(symmetricKey);
        }

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
        installed = true;

        return concatBytes(x25519Public, ed25519Public);
    } catch {
        return new Uint8Array(0);
    } finally {
        if (!installed) {
            // Cache install failed — clear any retained buffers so secret
            // material doesn't linger until GC.
            if (x25519Private !== null) {
                clearBytes(x25519Private);
            }
            if (x25519Public !== null) {
                clearBytes(x25519Public);
            }
            if (ed25519Public !== null) {
                clearBytes(ed25519Public);
            }
        }
    }
}

/**
 * Get public keys for a cached key set.
 * Returns: [x25519Pub(32) | ed25519Pub(32)] = 64 bytes, or empty on error.
 */
export function getPublicKeys(keyId: string): Uint8Array {
    const keys = getCachedKeys(keyId);
    if (!keys) {
        return new Uint8Array(0);
    }
    return concatBytes(keys.x25519Public, keys.ed25519Public);
}

/**
 * Query-pure: does NOT evict expired entries (call `getCachedKeys` for that).
 * Used as a probe from C# `HasCachedKey` to drive cache-warmth gates; an
 * eviction side-effect here would race against follow-up cached ops in the
 * same tick.
 */
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

/** Returns: signature(64) */
export async function ed25519Sign(message: Uint8Array, privateKey: Uint8Array): Promise<Uint8Array> {
    return await coreEd25519Sign(message, privateKey);
}

/** Returns: boolean */
export async function ed25519Verify(signature: Uint8Array, message: Uint8Array, publicKey: Uint8Array): Promise<boolean> {
    return await coreEd25519Verify(signature, message, publicKey);
}

/** Returns: [x25519Priv(32) | x25519Pub(32) | ed25519Priv(32) | ed25519Pub(32)] = 128 bytes */
export async function deriveDualKeyPair(seed: Uint8Array): Promise<Uint8Array> {
    const dual = await coreDeriveDualKeyPair(seed);
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
export async function deriveWrappingKey(ownPrivateKey: Uint8Array, recipientPublicKey: Uint8Array, context: string): Promise<Uint8Array> {
    return await coreDeriveWrappingKey(ownPrivateKey, recipientPublicKey, context);
}

/** Returns: randomBytes(N) */
export function generateRandomBytes(length: number): Uint8Array {
    return coreGenerateRandomBytes(length);
}

/** Feature-presence check for SubtleCrypto. All crypto operations route
 * through <c>crypto.subtle</c>; if it isn't present nothing else will work. */
export function isSupported(): boolean {
    return typeof crypto !== 'undefined' && typeof crypto.subtle !== 'undefined';
}

// ============================================================
// BASE64 BRIDGE FUNCTIONS (for JSImport — packed binary as Base64 strings)
// These are the exports consumed by C# via CryptoInterop.cs.
// ============================================================

/**
 * Base64(signature(64)). privKey crosses as a .NET MemoryView — slice into a
 * real Uint8Array, then zeroize that copy in finally.
 */
export async function ed25519SignB64(msgB64: string, privKey: IMemoryView): Promise<string> {
    const priv = privKey.slice();
    try {
        return bytesToBase64(await ed25519Sign(base64ToBytes(msgB64), priv));
    } finally {
        clearBytes(priv);
    }
}

export async function ed25519VerifyB64(sigB64: string, msgB64: string, pubB64: string): Promise<boolean> {
    return await ed25519Verify(base64ToBytes(sigB64), base64ToBytes(msgB64), base64ToBytes(pubB64));
}

/**
 * Base64([x25519Priv(32)|x25519Pub(32)|ed25519Priv(32)|ed25519Pub(32)]).
 * The seed crosses as a .NET MemoryView; slice into a real Uint8Array and
 * zeroize that copy in finally so the seed never lingers on the JS heap.
 * The packed dual-key result also contains private-key material — clear it
 * after Base64 encoding so it doesn't survive until GC.
 */
/**
 * Derive [x25519 + ed25519] keypair into the caller-allocated 128-byte
 * output MemoryView. Both seed and output bytes never land in a Base64
 * string on either heap (P21). Caller-supplied output layout matches
 * <c>CryptoInterop.DeriveDualKeyPairIntoAsync</c>.
 */
export async function deriveDualKeyPair_into(seed: IMemoryView, output: IMemoryView): Promise<void> {
    const seedCopy = seed.slice();
    try {
        const result = await deriveDualKeyPair(seedCopy);
        try {
            output.set(result);
        } finally {
            clearBytes(result);
        }
    } finally {
        clearBytes(seedCopy);
    }
}

/**
 * Base64([nonce(12)|ciphertext]). Both plaintext and key cross as .NET
 * MemoryViews; slice into real Uint8Arrays and zeroize both JS-owned copies
 * in finally. WrapContentKey routes a CEK through this path, so the plaintext
 * slice must be cleared too — the caller's buffer is independent.
 */
export async function encryptAesGcmB64(plaintext: IMemoryView, key: IMemoryView, aad: string | null = null): Promise<string> {
    const ptCopy = plaintext.slice();
    const keyCopy = key.slice();
    try {
        return bytesToBase64(await encryptAesGcm(ptCopy, keyCopy, aad));
    } finally {
        clearBytes(ptCopy);
        clearBytes(keyCopy);
    }
}

/**
 * Base64(plaintext). key crosses as a .NET MemoryView; slice + zeroize.
 * UnwrapContentKey routes a wrapped CEK through this path, so the plaintext
 * result is sensitive — clear it after Base64 encoding.
 */
export async function decryptAesGcmB64(ctB64: string, nonceB64: string, key: IMemoryView, aad: string | null = null): Promise<string> {
    const keyCopy = key.slice();
    try {
        const result = await decryptAesGcm(base64ToBytes(ctB64), base64ToBytes(nonceB64), keyCopy, aad);
        try {
            return bytesToBase64(result);
        } finally {
            clearBytes(result);
        }
    } finally {
        clearBytes(keyCopy);
    }
}

/**
 * Writes plaintext directly into the caller-allocated <c>output</c> MemoryView
 * rather than returning a Base64 string. P21 — no secret-bearing string on
 * either heap.
 */
export async function decryptAesGcm_into(
    ctB64: string, nonceB64: string,
    key: IMemoryView, output: IMemoryView,
    aad: string | null = null): Promise<void> {
    const keyCopy = key.slice();
    try {
        const result = await decryptAesGcm(base64ToBytes(ctB64), base64ToBytes(nonceB64), keyCopy, aad);
        try {
            output.set(result);
        } finally {
            clearBytes(result);
        }
    } finally {
        clearBytes(keyCopy);
    }
}

/** Base64([ephPubKey(32)|nonce(12)|ciphertext]) */
export async function encryptAsymmetricB64(ptB64: string, recipPubB64: string): Promise<string> {
    return bytesToBase64(await encryptAsymmetricAesGcm(base64ToBytes(ptB64), base64ToBytes(recipPubB64)));
}

/**
 * ECIES encrypt where plaintext crosses as a .NET MemoryView (P21 — for
 * secret-key plaintext). Slice + clear the local copy in finally so the
 * wrap key / PRF seed never sits in a JS-side string. Output Base64
 * carries [eph(32)|nonce(12)|ciphertext] — no secret on its own.
 */
export async function encryptAsymmetricFromBytesB64(plaintext: IMemoryView, recipPubB64: string): Promise<string> {
    const pt = plaintext.slice();
    try {
        return bytesToBase64(await encryptAsymmetricAesGcm(pt, base64ToBytes(recipPubB64)));
    } finally {
        clearBytes(pt);
    }
}

/**
 * Bytes-out ECIES decrypt: writes plaintext directly into the caller-allocated
 * <c>output</c> MemoryView. The K_wrap unwrap on the disk-import path routes
 * through here — no Base64 string carrying the unwrapped 32-byte secret on
 * either heap.
 */
export async function decryptAsymmetric_into(
    ephPubB64: string, ctB64: string, nonceB64: string,
    privKey: IMemoryView, output: IMemoryView): Promise<void> {
    const priv = privKey.slice();
    try {
        const plaintext = await decryptAsymmetricAesGcm(
            base64ToBytes(ephPubB64), base64ToBytes(ctB64), base64ToBytes(nonceB64), priv);
        try {
            output.set(plaintext);
        } finally {
            clearBytes(plaintext);
        }
    } finally {
        clearBytes(priv);
    }
}

/**
 * Bytes-out bridge surface (P21 phase 5+): writes the derived wrapping key
 * directly into the caller-allocated output MemoryView. No Base64 string
 * carrying the secret on either heap. Caller must size <c>output</c> to
 * 32 bytes.
 */
export async function deriveWrappingKey_into(ownPrivateKey: IMemoryView, recipPubB64: string, ctx: string, output: IMemoryView): Promise<void> {
    const priv = ownPrivateKey.slice();
    try {
        const result = await deriveWrappingKey(priv, base64ToBytes(recipPubB64), ctx);
        try {
            output.set(result);
        } finally {
            clearBytes(result);
        }
    } finally {
        clearBytes(priv);
    }
}

/**
 * Generate <c>output.length</c> random bytes directly into the caller-allocated
 * MemoryView. Random bytes are secret-equivalent (used for CEKs, K_wrap, salts)
 * — no Base64 string on either heap (P21).
 */
export function generateRandomBytes_into(output: IMemoryView): void {
    const result = generateRandomBytes(output.length);
    try {
        output.set(result);
    } finally {
        clearBytes(result);
    }
}

/**
 * Base64([x25519Pub(32)|ed25519Pub(32)]). The seed crosses as a .NET
 * MemoryView; slice into a real Uint8Array and zeroize that copy in finally
 * so the PRF seed never lingers on the JS heap.
 */
export async function storeKeysB64(keyId: string, seed: IMemoryView, ttlMs: number | null): Promise<string> {
    const seedCopy = seed.slice();
    try {
        return bytesToBase64(await storeKeys(keyId, seedCopy, ttlMs));
    } finally {
        clearBytes(seedCopy);
    }
}
export function getPublicKeysB64(keyId: string): string { return bytesToBase64(getPublicKeys(keyId)); }

/** Base64(signature(64)) */
export async function signWithCachedKeyB64(keyId: string, msgB64: string): Promise<string> {
    return bytesToBase64(await signWithCachedKey(keyId, base64ToBytes(msgB64)));
}

/** Base64(plaintext) */
export async function decryptAsymmetricCachedB64(keyId: string, ephPubB64: string, ctB64: string, nonceB64: string): Promise<string> {
    return bytesToBase64(await decryptAsymmetricCachedAesGcm(keyId, base64ToBytes(ephPubB64), base64ToBytes(ctB64), base64ToBytes(nonceB64)));
}

/**
 * Bytes-out cached ECIES decrypt. The cached X25519 private key remains in
 * the JS-side key cache; only plaintext bytes are written to caller-owned
 * memory. Used for K_wrap unwrap on disk import.
 */
export async function decryptAsymmetricCached_into(
    keyId: string, ephPubB64: string, ctB64: string, nonceB64: string, output: IMemoryView
): Promise<void> {
    const plaintext = await decryptAsymmetricCachedAesGcm(
        keyId,
        base64ToBytes(ephPubB64),
        base64ToBytes(ctB64),
        base64ToBytes(nonceB64));
    try {
        if (plaintext.length !== output.length) {
            throw new Error(
                `decryptAsymmetricCached_into: output length ${output.length} does not match plaintext length ${plaintext.length}`);
        }
        output.set(plaintext);
    } finally {
        clearBytes(plaintext);
    }
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
/**
 * Bytes-out variant of generateVapidKeyPair — writes the packed
 * <c>[publicKey(65) | privateKeyPkcs8(N)]</c> directly into the caller-
 * allocated <c>output</c> MemoryView and returns the number of bytes written.
 * The PKCS8 priv-key bytes never land in a Base64 string on either heap (P21).
 */
export async function generateVapidKeyPair_into(output: IMemoryView): Promise<number> {
    const result = await generateVapidKeyPair();
    try {
        if (result.length > output.length) {
            return -1;  // signals caller to grow the buffer
        }
        output.set(result);
        return result.length;
    } finally {
        clearBytes(result);
    }
}

/** Import VAPID keypair from Base64 components, returns "true"/"false" */
export async function importVapidKeyPairB64(publicKeyB64: string, pkcs8PrivateKey: IMemoryView): Promise<boolean> {
    // pkcs8 priv crosses as a .NET MemoryView (P21 — no Base64 string on the
    // JS heap carrying the secret). Slice into a real Uint8Array, zeroize in
    // finally so the JS-side copy doesn't linger.
    const pkcs8 = pkcs8PrivateKey.slice();
    try {
        return await importVapidKeyPair(base64ToBytes(publicKeyB64), pkcs8);
    } finally {
        clearBytes(pkcs8);
    }
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
