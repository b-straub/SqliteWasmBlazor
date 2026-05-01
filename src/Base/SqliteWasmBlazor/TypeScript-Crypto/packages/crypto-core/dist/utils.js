// @sqlitewasmblazor/crypto-core — consolidated encoding/memory utilities
// ============================================================
// BASE64 ENCODING
// ============================================================
/**
 * Decode standard Base64 string to Uint8Array.
 */
export function base64ToBytes(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}
/**
 * Encode Uint8Array to standard Base64 string.
 */
export function bytesToBase64(bytes) {
    let binary = '';
    for (let i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}
/**
 * Convert URL-safe Base64 to standard Base64.
 */
export function base64UrlToBase64(base64url) {
    return base64url
        .replace(/-/g, '+')
        .replace(/_/g, '/')
        .padEnd(base64url.length + (4 - base64url.length % 4) % 4, '=');
}
// ============================================================
// MEMORY SAFETY
// ============================================================
/**
 * Zero-fill a Uint8Array to securely wipe sensitive data from memory.
 */
export function clearBytes(bytes) {
    bytes.fill(0);
}
/**
 * Execute a function with a buffer, ensuring it's zeroed after use.
 */
export async function withSecureBuffer(buffer, fn) {
    try {
        return await fn(buffer);
    }
    finally {
        clearBytes(buffer);
    }
}
// ============================================================
// BYTE MANIPULATION
// ============================================================
/**
 * Concatenate multiple Uint8Arrays into a single buffer.
 */
export function concatBytes(...arrays) {
    const totalLength = arrays.reduce((acc, arr) => acc + arr.length, 0);
    const result = new Uint8Array(totalLength);
    let offset = 0;
    for (const arr of arrays) {
        result.set(arr, offset);
        offset += arr.length;
    }
    return result;
}
// ============================================================
// SUBTLE CRYPTO BOUNDARY
// ============================================================
/**
 * Convert Uint8Array to ArrayBuffer for SubtleCrypto APIs.
 * TS 5.7+ distinguishes Uint8Array<ArrayBufferLike> from BufferSource,
 * requiring an explicit slice + cast at the SubtleCrypto boundary.
 */
export function toBuffer(data) {
    return data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength);
}
// ============================================================
// KEY FORMAT HELPERS
// ============================================================
/**
 * Wrap Ed25519 seed in PKCS8 ASN.1 structure for SubtleCrypto importKey.
 */
export function wrapSeedInPkcs8(seed) {
    const pkcs8Header = new Uint8Array([
        0x30, 0x2e, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06,
        0x03, 0x2b, 0x65, 0x70, 0x04, 0x22, 0x04, 0x20
    ]);
    const pkcs8Key = new Uint8Array(pkcs8Header.length + seed.length);
    pkcs8Key.set(pkcs8Header);
    pkcs8Key.set(seed, pkcs8Header.length);
    return pkcs8Key;
}
// ============================================================
// RANDOM
// ============================================================
/**
 * Generate cryptographically secure random bytes.
 */
export function generateRandomBytes(length) {
    return crypto.getRandomValues(new Uint8Array(length));
}
//# sourceMappingURL=utils.js.map