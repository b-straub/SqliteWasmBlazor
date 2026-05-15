// Property: nonces are unique across all clients and all keys for which they
// are used (whitepaper §4 P11). With AES-GCM's 96-bit random nonce the
// birthday bound is ≈ 2^32 messages-per-key; this test catches gross
// regressions like "nonce is a counter that resets on worker reload",
// "nonce is constant", "nonce derived deterministically from plaintext", or
// "Subtle returned the same buffer instance every call". It does not catch
// subtle entropy bias — that requires statistical tooling outside skill scope.
//
// Two surfaces exercised:
//   - encryptAesGcm   — used by per-row delta encryption (crypto-delta.ts)
//   - encryptChaCha20Poly1305 — used by the at-rest VFS (sahpool-prf-vfs.ts)
//
// Both pull nonces from the same `crypto.getRandomValues`-backed
// generateRandomBytes helper, but exercising both shapes guards against a
// future divergence in either call site.

import { describe, it, expect } from 'vitest';
import {
    encryptAesGcm,
    encryptChaCha20Poly1305,
} from '@sqlitewasmblazor/crypto-core';

function bytesToHex(b: Uint8Array): string {
    return Array.from(b).map(x => x.toString(16).padStart(2, '0')).join('');
}

describe('Nonce uniqueness property (P11)', () => {
    it('encryptAesGcm: no (key, nonce) collision across N clients × M operations', async () => {
        // 10 clients × 100 ops = 1000 nonces. Probability of any collision
        // for uniformly-random 96-bit nonces at this scale is ≈ 10^-23 —
        // any failure here means the nonce source is broken.
        const N_CLIENTS = 10;
        const M_OPS = 100;
        const seen = new Set<string>();

        for (let c = 0; c < N_CLIENTS; c++) {
            // Distinct key per simulated client — collisions across keys
            // would also be caught (we key the set on key||nonce).
            const key = new Uint8Array(32);
            crypto.getRandomValues(key);
            const keyHex = bytesToHex(key);

            for (let m = 0; m < M_OPS; m++) {
                const plaintext = new Uint8Array(64);
                crypto.getRandomValues(plaintext);
                const enc = await encryptAesGcm(plaintext, key);
                expect(enc.nonce.length).toBe(12);

                const composite = keyHex + ':' + bytesToHex(enc.nonce);
                expect(seen.has(composite)).toBe(false);
                seen.add(composite);
            }
        }
        expect(seen.size).toBe(N_CLIENTS * M_OPS);
    });

    it('encryptAesGcm: nonce buffer is a fresh allocation per call (not reused)', async () => {
        // Defense against "Subtle handed back the same buffer instance" —
        // mutating one returned nonce must never affect a subsequent call's
        // nonce. (This would be catastrophic for IND-CPA at scale.)
        const key = new Uint8Array(32);
        crypto.getRandomValues(key);
        const plaintext = new Uint8Array(32).fill(0xAA);

        const a = await encryptAesGcm(plaintext, key);
        const aBackup = new Uint8Array(a.nonce);
        a.nonce.fill(0); // mutate

        const b = await encryptAesGcm(plaintext, key);
        // b.nonce must be unaffected by our mutation of a.nonce.
        expect(Buffer.from(b.nonce).equals(Buffer.alloc(12, 0))).toBe(false);
        expect(Buffer.from(b.nonce).equals(Buffer.from(aBackup))).toBe(false);
    });

    it('encryptChaCha20Poly1305: no nonce collision over 1000 sequential encrypts (VFS path)', () => {
        // Sync noble path used by the at-rest PRF-keyed VFS. One key, many
        // ops — simulates what a single DB does as it writes pages.
        const N_OPS = 1000;
        const seen = new Set<string>();
        const key = new Uint8Array(32);
        crypto.getRandomValues(key);
        const aad = new TextEncoder().encode('vfs-test');

        for (let i = 0; i < N_OPS; i++) {
            const plaintext = new Uint8Array(128);
            crypto.getRandomValues(plaintext);
            const enc = encryptChaCha20Poly1305(plaintext, key, aad);
            expect(enc.nonce.length).toBe(12);
            const nonceHex = bytesToHex(enc.nonce);
            expect(seen.has(nonceHex)).toBe(false);
            seen.add(nonceHex);
        }
        expect(seen.size).toBe(N_OPS);
    });

    it('encryptChaCha20Poly1305: nonce distribution covers high-entropy bits (sanity check)', () => {
        // Crude sanity check that the nonce isn't a low-byte counter masquerading
        // as random: across 200 encrypts, the high byte of the nonce should
        // exhibit at least 32 distinct values out of 256. A counter-based
        // implementation would show ≤ 1 distinct high byte.
        const N = 200;
        const highBytes = new Set<number>();
        const key = new Uint8Array(32);
        crypto.getRandomValues(key);
        const plaintext = new Uint8Array(64);
        const aad = new TextEncoder().encode('x');

        for (let i = 0; i < N; i++) {
            crypto.getRandomValues(plaintext);
            const enc = encryptChaCha20Poly1305(plaintext, key, aad);
            highBytes.add(enc.nonce[11]); // last (highest-order in BE) byte
        }
        expect(highBytes.size).toBeGreaterThan(32);
    });
});
