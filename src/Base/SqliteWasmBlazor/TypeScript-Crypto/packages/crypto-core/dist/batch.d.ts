/**
 * Compute SHA-256 digest over paired ciphertext+nonce arrays for batch signing.
 * The digest binds the signature to every encrypted row in the batch.
 * Uses incremental hashing — O(1) memory regardless of batch size.
 * Pairs are fed in order: update(ct0), update(nonce0), update(ct1), update(nonce1), ...
 */
export declare function computeBatchDigest(ciphertexts: Uint8Array[], nonces: Uint8Array[]): Uint8Array;
/**
 * Sign a batch of encrypted rows. Computes the batch digest internally
 * and signs with Ed25519.
 * Returns the 64-byte signature.
 */
export declare function signBatch(ciphertexts: Uint8Array[], nonces: Uint8Array[], ed25519PrivateKey: Uint8Array): Uint8Array;
/**
 * Verify a batch signature. Recomputes the batch digest from the
 * ciphertexts/nonces and verifies against the provided signature.
 * Returns true if valid.
 */
export declare function verifyBatch(ciphertexts: Uint8Array[], nonces: Uint8Array[], signature: Uint8Array, ed25519PublicKey: Uint8Array): boolean;
//# sourceMappingURL=batch.d.ts.map