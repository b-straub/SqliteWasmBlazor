// @sqlitewasmblazor/crypto-core — HKDF key derivation

import { sha256 } from '@awasm/noble';
import { hkdf } from '@awasm/noble/hkdf.js';
import { clearBytes } from './utils.js';
import { x25519SharedSecret, getX25519PublicKey } from './x25519.js';
import { getEd25519PublicKey } from './ed25519.js';
import type { KeyPair, DualKeyPairFull } from './types.js';

const encoder = new TextEncoder();

// HKDF info strings — must match crypto-bridge.ts values
const X25519_INFO = encoder.encode('x25519-key');
const ED25519_INFO = encoder.encode('ed25519-key');

/**
 * Derive a key from seed using HKDF-SHA256.
 * Caller must clearBytes the result when done.
 */
export function deriveHkdfKey(seed: Uint8Array, info: string, length: number = 32): Uint8Array {
    return hkdf(sha256, seed, undefined, encoder.encode(info), length);
}

/**
 * Derive X25519 key pair from PRF seed using HKDF.
 * Caller must clearBytes(result.privateKey) when done.
 */
export async function deriveX25519KeyPair(seed: Uint8Array): Promise<KeyPair> {
    const privateKey = hkdf(sha256, seed, undefined, X25519_INFO, 32);
    const publicKey = await getX25519PublicKey(privateKey);
    return { privateKey, publicKey };
}

/**
 * Derive Ed25519 key pair from PRF seed using HKDF.
 * Caller must clearBytes(result.privateKey) when done.
 */
export async function deriveEd25519KeyPair(seed: Uint8Array): Promise<KeyPair> {
    const privateKey = hkdf(sha256, seed, undefined, ED25519_INFO, 32);
    const publicKey = await getEd25519PublicKey(privateKey);
    return { privateKey, publicKey };
}

/**
 * Derive both X25519 and Ed25519 key pairs from a single PRF seed.
 * Caller must clearBytes all private keys when done.
 */
export async function deriveDualKeyPair(seed: Uint8Array): Promise<DualKeyPairFull> {
    const x25519PrivateKey = hkdf(sha256, seed, undefined, X25519_INFO, 32);
    const x25519PublicKey = await getX25519PublicKey(x25519PrivateKey);

    const ed25519PrivateKey = hkdf(sha256, seed, undefined, ED25519_INFO, 32);
    const ed25519PublicKey = await getEd25519PublicKey(ed25519PrivateKey);

    return { x25519PrivateKey, x25519PublicKey, ed25519PrivateKey, ed25519PublicKey };
}

/**
 * Derive a wrapping key via X25519 ECDH + HKDF-SHA256.
 * Combines key agreement and key derivation in one call.
 * Caller must clearBytes the result when done.
 */
export async function deriveWrappingKey(
    ownPrivateKey: Uint8Array,
    recipientPublicKey: Uint8Array,
    context: string
): Promise<Uint8Array> {
    const sharedSecret = await x25519SharedSecret(ownPrivateKey, recipientPublicKey);
    const wrappingKey = hkdf(sha256, sharedSecret, undefined, encoder.encode(context), 32);
    clearBytes(sharedSecret);
    return wrappingKey;
}
