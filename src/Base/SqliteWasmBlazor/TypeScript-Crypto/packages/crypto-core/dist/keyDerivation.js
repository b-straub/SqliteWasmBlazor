// @sqlitewasmblazor/crypto-core — HKDF key derivation
import { x25519 } from '@noble/curves/ed25519';
import { ed25519 } from '@noble/curves/ed25519';
import { sha256 } from '@awasm/noble';
import { hkdf } from '@awasm/noble/hkdf.js';
import { clearBytes } from './utils.js';
import { x25519SharedSecret } from './x25519.js';
const encoder = new TextEncoder();
// HKDF info strings — must match existing crypto.ts values
const X25519_INFO = encoder.encode('x25519-key');
const ED25519_INFO = encoder.encode('ed25519-key');
/**
 * Derive a key from seed using HKDF-SHA256.
 * Caller must clearBytes the result when done.
 */
export function deriveHkdfKey(seed, info, length = 32) {
    return hkdf(sha256, seed, undefined, encoder.encode(info), length);
}
/**
 * Derive X25519 key pair from PRF seed using HKDF.
 * Caller must clearBytes(result.privateKey) when done.
 */
export function deriveX25519KeyPair(seed) {
    const privateKey = hkdf(sha256, seed, undefined, X25519_INFO, 32);
    const publicKey = x25519.getPublicKey(privateKey);
    return { privateKey, publicKey };
}
/**
 * Derive Ed25519 key pair from PRF seed using HKDF.
 * Caller must clearBytes(result.privateKey) when done.
 */
export function deriveEd25519KeyPair(seed) {
    const privateKey = hkdf(sha256, seed, undefined, ED25519_INFO, 32);
    const publicKey = ed25519.getPublicKey(privateKey);
    return { privateKey, publicKey };
}
/**
 * Derive both X25519 and Ed25519 key pairs from a single PRF seed.
 * Caller must clearBytes all private keys when done.
 */
export function deriveDualKeyPair(seed) {
    const x25519PrivateKey = hkdf(sha256, seed, undefined, X25519_INFO, 32);
    const x25519PublicKey = x25519.getPublicKey(x25519PrivateKey);
    const ed25519PrivateKey = hkdf(sha256, seed, undefined, ED25519_INFO, 32);
    const ed25519PublicKey = ed25519.getPublicKey(ed25519PrivateKey);
    return { x25519PrivateKey, x25519PublicKey, ed25519PrivateKey, ed25519PublicKey };
}
/**
 * Derive a wrapping key via X25519 ECDH + HKDF-SHA256.
 * Combines key agreement and key derivation in one call.
 * Caller must clearBytes the result when done.
 */
export function deriveWrappingKey(ownPrivateKey, recipientPublicKey, context) {
    const sharedSecret = x25519SharedSecret(ownPrivateKey, recipientPublicKey);
    const wrappingKey = hkdf(sha256, sharedSecret, undefined, encoder.encode(context), 32);
    clearBytes(sharedSecret);
    return wrappingKey;
}
//# sourceMappingURL=keyDerivation.js.map