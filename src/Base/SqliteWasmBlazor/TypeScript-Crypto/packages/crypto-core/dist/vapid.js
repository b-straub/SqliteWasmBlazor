// @sqlitewasmblazor/crypto-core — VAPID (Voluntary Application Server Identification)
// ES256 JWT signing using Web Crypto API (ECDSA P-256 + SHA-256)
import { toBuffer } from './utils.js';
/**
 * Generate a new VAPID ECDSA P-256 key pair.
 */
export async function generateVapidKeyPair() {
    const keyPair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign']);
    const publicKeyRaw = new Uint8Array(await crypto.subtle.exportKey('raw', keyPair.publicKey));
    const privateKeyPkcs8 = new Uint8Array(await crypto.subtle.exportKey('pkcs8', keyPair.privateKey));
    return { publicKey: publicKeyRaw, privateKeyPkcs8, cryptoKey: keyPair.privateKey };
}
/**
 * Import a VAPID private key from PKCS8 bytes.
 * Returns a non-extractable CryptoKey for signing.
 */
export async function importVapidPrivateKey(pkcs8) {
    return crypto.subtle.importKey('pkcs8', toBuffer(pkcs8), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign']);
}
/**
 * Create a signed VAPID Authorization header value.
 *
 * @param audience - Push service origin (e.g. "https://fcm.googleapis.com")
 * @param subject - Contact URI (e.g. "mailto:user@example.com")
 * @param privateKey - ECDSA P-256 CryptoKey for signing
 * @param publicKey - Raw 65-byte uncompressed public key
 * @param expSeconds - JWT expiry in seconds from now (default 12 hours, max 24h per spec)
 * @returns The full Authorization header value: "vapid t=<JWT>, k=<base64url pubkey>"
 */
export async function createVapidAuthHeader(audience, subject, privateKey, publicKey, expSeconds = 12 * 60 * 60) {
    const jwt = await signVapidJwt(audience, subject, privateKey, expSeconds);
    const k = uint8ArrayToBase64Url(publicKey);
    return `vapid t=${jwt}, k=${k}`;
}
/**
 * Sign a VAPID JWT (ES256).
 */
async function signVapidJwt(audience, subject, privateKey, expSeconds) {
    const header = { typ: 'JWT', alg: 'ES256' };
    const now = Math.floor(Date.now() / 1000);
    const payload = {
        aud: audience,
        exp: now + expSeconds,
        sub: subject,
    };
    const headerB64 = jsonToBase64Url(header);
    const payloadB64 = jsonToBase64Url(payload);
    const signingInput = `${headerB64}.${payloadB64}`;
    const signatureBuffer = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, privateKey, new TextEncoder().encode(signingInput));
    // Web Crypto returns IEEE P1363 format (r || s, 64 bytes) which is what JWT ES256 expects
    const signatureB64 = uint8ArrayToBase64Url(new Uint8Array(signatureBuffer));
    return `${signingInput}.${signatureB64}`;
}
// ============================================================
// BASE64URL HELPERS (JWT-specific, not exported from package)
// ============================================================
function jsonToBase64Url(obj) {
    const json = JSON.stringify(obj);
    const bytes = new TextEncoder().encode(json);
    return uint8ArrayToBase64Url(bytes);
}
function uint8ArrayToBase64Url(bytes) {
    let binary = '';
    for (let i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary)
        .replace(/\+/g, '-')
        .replace(/\//g, '_')
        .replace(/=+$/, '');
}
//# sourceMappingURL=vapid.js.map