import { describe, it, expect } from 'vitest';
import {
    computeBatchDigest, signBatch, verifyBatch,
    generateEd25519KeyPair, generateRandomBytes,
    encryptAesGcm,
} from '../src/index.js';

describe('batch signatures', () => {
    // Helper: generate N encrypted rows
    async function generateEncryptedRows(count: number) {
        const key = generateRandomBytes(32);
        const ciphertexts: Uint8Array[] = [];
        const nonces: Uint8Array[] = [];
        for (let i = 0; i < count; i++) {
            const plaintext = new TextEncoder().encode(`row-${i}`);
            const encrypted = await encryptAesGcm(plaintext, key);
            ciphertexts.push(encrypted.ciphertext);
            nonces.push(encrypted.nonce);
        }
        return { ciphertexts, nonces };
    }

    it('computeBatchDigest produces 32-byte hash', async () => {
        const { ciphertexts, nonces } = await generateEncryptedRows(5);
        const digest = computeBatchDigest(ciphertexts, nonces);
        expect(digest.length).toBe(32);
    });

    it('computeBatchDigest is deterministic', async () => {
        const { ciphertexts, nonces } = await generateEncryptedRows(3);
        const d1 = computeBatchDigest(ciphertexts, nonces);
        const d2 = computeBatchDigest(ciphertexts, nonces);
        expect(d1).toEqual(d2);
    });

    it('computeBatchDigest changes when row order changes', async () => {
        const { ciphertexts, nonces } = await generateEncryptedRows(3);
        const d1 = computeBatchDigest(ciphertexts, nonces);

        // Swap first and last
        const swappedCt = [ciphertexts[2], ciphertexts[1], ciphertexts[0]];
        const swappedN = [nonces[2], nonces[1], nonces[0]];
        const d2 = computeBatchDigest(swappedCt, swappedN);

        expect(d1).not.toEqual(d2);
    });

    it('computeBatchDigest rejects mismatched lengths', () => {
        const ct = [generateRandomBytes(32)];
        const n = [generateRandomBytes(12), generateRandomBytes(12)];
        expect(() => computeBatchDigest(ct, n)).toThrow(/Mismatched/);
    });

    it('signBatch/verifyBatch round-trip', async () => {
        const kp = generateEd25519KeyPair();
        const { ciphertexts, nonces } = await generateEncryptedRows(10);

        const sig = signBatch(ciphertexts, nonces, kp.privateKey);
        expect(sig.length).toBe(64);

        expect(verifyBatch(ciphertexts, nonces, sig, kp.publicKey)).toBe(true);
    });

    it('verifyBatch rejects tampered ciphertext', async () => {
        const kp = generateEd25519KeyPair();
        const { ciphertexts, nonces } = await generateEncryptedRows(5);

        const sig = signBatch(ciphertexts, nonces, kp.privateKey);

        // Tamper with one row
        const tampered = [...ciphertexts];
        tampered[2] = generateRandomBytes(tampered[2].length);

        expect(verifyBatch(tampered, nonces, sig, kp.publicKey)).toBe(false);
    });

    it('verifyBatch rejects tampered nonce', async () => {
        const kp = generateEd25519KeyPair();
        const { ciphertexts, nonces } = await generateEncryptedRows(5);

        const sig = signBatch(ciphertexts, nonces, kp.privateKey);

        const tampered = [...nonces];
        tampered[0] = generateRandomBytes(12);

        expect(verifyBatch(ciphertexts, tampered, sig, kp.publicKey)).toBe(false);
    });

    it('verifyBatch rejects wrong sender key', async () => {
        const sender = generateEd25519KeyPair();
        const attacker = generateEd25519KeyPair();
        const { ciphertexts, nonces } = await generateEncryptedRows(3);

        const sig = signBatch(ciphertexts, nonces, sender.privateKey);

        expect(verifyBatch(ciphertexts, nonces, sig, attacker.publicKey)).toBe(false);
    });

    it('verifyBatch rejects added row', async () => {
        const kp = generateEd25519KeyPair();
        const { ciphertexts, nonces } = await generateEncryptedRows(3);

        const sig = signBatch(ciphertexts, nonces, kp.privateKey);

        // Add an extra row
        const extraCt = [...ciphertexts, generateRandomBytes(32)];
        const extraN = [...nonces, generateRandomBytes(12)];

        expect(verifyBatch(extraCt, extraN, sig, kp.publicKey)).toBe(false);
    });

    it('verifyBatch rejects removed row', async () => {
        const kp = generateEd25519KeyPair();
        const { ciphertexts, nonces } = await generateEncryptedRows(3);

        const sig = signBatch(ciphertexts, nonces, kp.privateKey);

        // Remove last row
        expect(verifyBatch(ciphertexts.slice(0, 2), nonces.slice(0, 2), sig, kp.publicKey)).toBe(false);
    });

    it('works with empty batch', () => {
        const kp = generateEd25519KeyPair();
        const sig = signBatch([], [], kp.privateKey);
        expect(sig.length).toBe(64);
        expect(verifyBatch([], [], sig, kp.publicKey)).toBe(true);
    });

    it('works with single row', async () => {
        const kp = generateEd25519KeyPair();
        const { ciphertexts, nonces } = await generateEncryptedRows(1);

        const sig = signBatch(ciphertexts, nonces, kp.privateKey);
        expect(verifyBatch(ciphertexts, nonces, sig, kp.publicKey)).toBe(true);
    });
});
