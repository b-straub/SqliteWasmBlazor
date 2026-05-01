import type { KeyPair, DualKeyPairFull } from './types.js';
/**
 * Derive a key from seed using HKDF-SHA256.
 * Caller must clearBytes the result when done.
 */
export declare function deriveHkdfKey(seed: Uint8Array, info: string, length?: number): Uint8Array;
/**
 * Derive X25519 key pair from PRF seed using HKDF.
 * Caller must clearBytes(result.privateKey) when done.
 */
export declare function deriveX25519KeyPair(seed: Uint8Array): KeyPair;
/**
 * Derive Ed25519 key pair from PRF seed using HKDF.
 * Caller must clearBytes(result.privateKey) when done.
 */
export declare function deriveEd25519KeyPair(seed: Uint8Array): KeyPair;
/**
 * Derive both X25519 and Ed25519 key pairs from a single PRF seed.
 * Caller must clearBytes all private keys when done.
 */
export declare function deriveDualKeyPair(seed: Uint8Array): DualKeyPairFull;
/**
 * Derive a wrapping key via X25519 ECDH + HKDF-SHA256.
 * Combines key agreement and key derivation in one call.
 * Caller must clearBytes the result when done.
 */
export declare function deriveWrappingKey(ownPrivateKey: Uint8Array, recipientPublicKey: Uint8Array, context: string): Uint8Array;
//# sourceMappingURL=keyDerivation.d.ts.map