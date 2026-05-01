// @sqlitewasmblazor/crypto-core — X25519 key exchange (ECDH)
import { x25519 } from '@noble/curves/ed25519';
import { generateRandomBytes } from './utils.js';
/**
 * Generate a random X25519 key pair.
 * Caller must clearBytes(result.privateKey) when done.
 */
export function generateX25519KeyPair() {
    const privateKey = generateRandomBytes(32);
    const publicKey = x25519.getPublicKey(privateKey);
    return { privateKey, publicKey };
}
/**
 * Derive X25519 public key from private key.
 */
export function getX25519PublicKey(privateKey) {
    return x25519.getPublicKey(privateKey);
}
/**
 * Perform X25519 key agreement (ECDH).
 * Caller must clearBytes the result when done.
 */
export function x25519SharedSecret(privateKey, publicKey) {
    return x25519.getSharedSecret(privateKey, publicKey);
}
//# sourceMappingURL=x25519.js.map