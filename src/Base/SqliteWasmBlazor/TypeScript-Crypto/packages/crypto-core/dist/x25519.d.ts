import type { KeyPair } from './types.js';
/**
 * Generate a random X25519 key pair.
 * Caller must clearBytes(result.privateKey) when done.
 */
export declare function generateX25519KeyPair(): KeyPair;
/**
 * Derive X25519 public key from private key.
 */
export declare function getX25519PublicKey(privateKey: Uint8Array): Uint8Array;
/**
 * Perform X25519 key agreement (ECDH).
 * Caller must clearBytes the result when done.
 */
export declare function x25519SharedSecret(privateKey: Uint8Array, publicKey: Uint8Array): Uint8Array;
//# sourceMappingURL=x25519.d.ts.map