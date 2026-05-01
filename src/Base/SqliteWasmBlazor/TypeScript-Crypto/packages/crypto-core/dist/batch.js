// @sqlitewasmblazor/crypto-core — Batch signature API for ShadowRowGroup
//
// A ShadowRowGroup is always from ONE sender. A single batch signature
// provides identical security to per-row signatures at O(1) cost.
import { sha256 } from '@awasm/noble';
import { ed25519Sign, ed25519Verify } from './ed25519.js';
/**
 * Compute SHA-256 digest over paired ciphertext+nonce arrays for batch signing.
 * The digest binds the signature to every encrypted row in the batch.
 * Uses incremental hashing — O(1) memory regardless of batch size.
 * Pairs are fed in order: update(ct0), update(nonce0), update(ct1), update(nonce1), ...
 */
export function computeBatchDigest(ciphertexts, nonces) {
    if (ciphertexts.length !== nonces.length) {
        throw new Error(`Mismatched arrays: ${ciphertexts.length} ciphertexts vs ${nonces.length} nonces`);
    }
    const hasher = sha256.create();
    for (let i = 0; i < ciphertexts.length; i++) {
        hasher.update(ciphertexts[i]);
        hasher.update(nonces[i]);
    }
    return hasher.digest();
}
/**
 * Sign a batch of encrypted rows. Computes the batch digest internally
 * and signs with Ed25519.
 * Returns the 64-byte signature.
 */
export function signBatch(ciphertexts, nonces, ed25519PrivateKey) {
    const digest = computeBatchDigest(ciphertexts, nonces);
    return ed25519Sign(digest, ed25519PrivateKey);
}
/**
 * Verify a batch signature. Recomputes the batch digest from the
 * ciphertexts/nonces and verifies against the provided signature.
 * Returns true if valid.
 */
export function verifyBatch(ciphertexts, nonces, signature, ed25519PublicKey) {
    const digest = computeBatchDigest(ciphertexts, nonces);
    return ed25519Verify(signature, digest, ed25519PublicKey);
}
//# sourceMappingURL=batch.js.map