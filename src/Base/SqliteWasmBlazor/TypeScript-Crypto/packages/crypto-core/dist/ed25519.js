// @sqlitewasmblazor/crypto-core — Ed25519 signing/verification
import { ed25519 } from '@noble/curves/ed25519';
import { generateRandomBytes } from './utils.js';
/**
 * Generate a random Ed25519 key pair.
 * Caller must clearBytes(result.privateKey) when done.
 */
export function generateEd25519KeyPair() {
    const privateKey = generateRandomBytes(32);
    const publicKey = ed25519.getPublicKey(privateKey);
    return { privateKey, publicKey };
}
/**
 * Derive Ed25519 public key from private key (32-byte seed).
 */
export function getEd25519PublicKey(privateKey) {
    return ed25519.getPublicKey(privateKey);
}
/**
 * Sign a message with Ed25519. Returns 64-byte signature.
 */
export function ed25519Sign(message, privateKey) {
    return ed25519.sign(message, privateKey);
}
/**
 * Verify an Ed25519 signature.
 */
export function ed25519Verify(signature, message, publicKey) {
    try {
        return ed25519.verify(signature, message, publicKey);
    }
    catch {
        return false;
    }
}
//# sourceMappingURL=ed25519.js.map