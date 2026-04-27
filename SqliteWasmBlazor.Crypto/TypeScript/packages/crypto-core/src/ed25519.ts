// @sqlitewasmblazor/crypto-core — Ed25519 signing/verification

import { ed25519 } from '@noble/curves/ed25519';
import { generateRandomBytes } from './utils.js';
import type { KeyPair } from './types.js';

/**
 * Generate a random Ed25519 key pair.
 * Caller must clearBytes(result.privateKey) when done.
 */
export function generateEd25519KeyPair(): KeyPair {
    const privateKey = generateRandomBytes(32);
    const publicKey = ed25519.getPublicKey(privateKey);
    return { privateKey, publicKey };
}

/**
 * Derive Ed25519 public key from private key (32-byte seed).
 */
export function getEd25519PublicKey(privateKey: Uint8Array): Uint8Array {
    return ed25519.getPublicKey(privateKey);
}

/**
 * Sign a message with Ed25519. Returns 64-byte signature.
 */
export function ed25519Sign(message: Uint8Array, privateKey: Uint8Array): Uint8Array {
    return ed25519.sign(message, privateKey);
}

/**
 * Verify an Ed25519 signature.
 */
export function ed25519Verify(signature: Uint8Array, message: Uint8Array, publicKey: Uint8Array): boolean {
    try {
        return ed25519.verify(signature, message, publicKey);
    } catch {
        return false;
    }
}
