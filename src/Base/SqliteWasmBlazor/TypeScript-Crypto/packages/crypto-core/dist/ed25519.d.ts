import type { KeyPair } from './types.js';
/**
 * Generate a random Ed25519 key pair.
 * Caller must clearBytes(result.privateKey) when done.
 */
export declare function generateEd25519KeyPair(): KeyPair;
/**
 * Derive Ed25519 public key from private key (32-byte seed).
 */
export declare function getEd25519PublicKey(privateKey: Uint8Array): Uint8Array;
/**
 * Sign a message with Ed25519. Returns 64-byte signature.
 */
export declare function ed25519Sign(message: Uint8Array, privateKey: Uint8Array): Uint8Array;
/**
 * Verify an Ed25519 signature.
 */
export declare function ed25519Verify(signature: Uint8Array, message: Uint8Array, publicKey: Uint8Array): boolean;
//# sourceMappingURL=ed25519.d.ts.map