// Property: AES-GCM under random nonces is IND-CPA at the storage layer
// (whitepaper §4 P3). Two equal-length plaintexts under the same key produce
// uncorrelated ciphertexts, and ciphertext length is exactly plaintext.length
// + 16 (the AEAD tag) — no length-correlated structure beyond AEAD overhead.
//
// Run as a seeded-RNG iterative property test rather than via fast-check to
// avoid adding a devDependency for this single suite. If fast-check is wired
// in a future change, this file's loop body is the property body and the
// outer iteration becomes `fc.assert(fc.asyncProperty(...))`.

import { describe, it, expect } from 'vitest';
import { encryptAesGcm } from '@sqlitewasmblazor/crypto-core';

// Mulberry32 — small fast seeded PRNG. Not cryptographic; only used to drive
// reproducible test inputs. Real crypto nonces still come from Subtle.
function mulberry32(seed: number): () => number {
    let s = seed >>> 0;
    return () => {
        s = (s + 0x6D2B79F5) | 0;
        let t = s;
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

function randomBytes(rng: () => number, n: number): Uint8Array {
    const b = new Uint8Array(n);
    for (let i = 0; i < n; i++) b[i] = Math.floor(rng() * 256);
    return b;
}

const N_ITER = 100;
const AAD = new TextEncoder().encode('test:v1:1');

describe('AES-GCM IND-CPA property (P3)', () => {
    it('two distinct equal-length plaintexts under same key produce distinct ciphertexts', async () => {
        const rng = mulberry32(0xC0FFEE);
        const key = randomBytes(rng, 32);
        let checked = 0;
        for (let i = 0; i < N_ITER; i++) {
            const len = 16 + Math.floor(rng() * 256); // 16..271 bytes
            const p1 = randomBytes(rng, len);
            const p2 = randomBytes(rng, len);
            // With 256-bit-plus inputs, p1 == p2 happens with probability ~0;
            // skip the degenerate iteration if it ever fires so the assertion
            // below remains meaningful.
            if (Buffer.from(p1).equals(Buffer.from(p2))) continue;
            const c1 = await encryptAesGcm(p1, key, AAD);
            const c2 = await encryptAesGcm(p2, key, AAD);
            expect(c1.ciphertext.length).toBe(len + 16);
            expect(c2.ciphertext.length).toBe(len + 16);
            expect(Buffer.from(c1.ciphertext).equals(Buffer.from(c2.ciphertext))).toBe(false);
            checked++;
        }
        expect(checked).toBeGreaterThan(N_ITER - 5);
    });

    it('encrypting same plaintext twice produces distinct ciphertexts (random nonce per call)', async () => {
        const rng = mulberry32(0xDEAD);
        const key = randomBytes(rng, 32);
        for (let i = 0; i < N_ITER; i++) {
            const len = 16 + Math.floor(rng() * 256);
            const p = randomBytes(rng, len);
            const c1 = await encryptAesGcm(p, key);
            const c2 = await encryptAesGcm(p, key);
            // Fresh nonce each call → ciphertexts differ even for identical input.
            expect(Buffer.from(c1.nonce).equals(Buffer.from(c2.nonce))).toBe(false);
            expect(Buffer.from(c1.ciphertext).equals(Buffer.from(c2.ciphertext))).toBe(false);
        }
    });

    it('ciphertext length is exactly plaintext.length + 16 (no length side channel beyond AEAD)', async () => {
        const rng = mulberry32(0xBEEF);
        const key = randomBytes(rng, 32);
        for (let i = 0; i < N_ITER; i++) {
            const len = Math.floor(rng() * 4096); // 0..4095 bytes — full SQLite-page range
            const p = randomBytes(rng, len);
            const c = await encryptAesGcm(p, key);
            expect(c.nonce.length).toBe(12);
            expect(c.ciphertext.length).toBe(len + 16);
        }
    });

    it('ciphertext length is independent of plaintext content (only of length)', async () => {
        const rng = mulberry32(0xFACE);
        const key = randomBytes(rng, 32);
        const FIXED_LEN = 256;
        const lengths = new Set<number>();
        for (let i = 0; i < N_ITER; i++) {
            const p = randomBytes(rng, FIXED_LEN);
            const c = await encryptAesGcm(p, key);
            lengths.add(c.ciphertext.length);
        }
        // If padding/compression varied with content, we'd see >1 distinct
        // ciphertext length for fixed-length inputs.
        expect(lengths.size).toBe(1);
        expect(lengths.values().next().value).toBe(FIXED_LEN + 16);
    });
});
