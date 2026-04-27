// Tests for the PRF-VFS crypto envelope + helper modules.
//
// Scope limits: a full SAHPool round-trip requires OPFS (browser-only), so
// we test the parts we can isolate from SQLite / OPFS:
//   - aad.ts: buildPageAad shape
//   - key-registry.ts: lifecycle + wipe
//   - envelope round-trip via @sqlitewasmblazor/crypto-core primitives
// End-to-end SQL tests live in the Demo / TestApp run (browser).

import { describe, it, expect } from 'vitest';
import {
    encryptChaCha20Poly1305,
    decryptChaCha20Poly1305,
} from '@sqlitewasmblazor/crypto-core';
import { buildPageAad } from '../aad.js';
import {
    registerKeyForPath,
    getKeyForPath,
    clearKeyForPath,
    isPathEncrypted,
} from '../key-registry.js';

const SECTOR_SIZE = 4096;
const PAGE_NONCE_LEN = 12;
const PAGE_TAG_LEN = 16;
const PAGE_ENVELOPE_TAIL = PAGE_NONCE_LEN + PAGE_TAG_LEN; // 28
const PAGE_PLAINTEXT_LEN = SECTOR_SIZE; // 4096 — full logical page
const PHYSICAL_SLOT_SIZE = SECTOR_SIZE + PAGE_ENVELOPE_TAIL; // 4124

function makeKey(seed: number): Uint8Array {
    const k = new Uint8Array(32);
    for (let i = 0; i < 32; i++) k[i] = (seed + i) & 0xff;
    return k;
}

describe('buildPageAad', () => {
    it('starts with the version prefix', () => {
        const aad = buildPageAad('/databases/contacts.db', 0);
        const decoded = new TextDecoder().decode(aad.subarray(0, 11));
        expect(decoded).toBe('prf-vfs-v1|');
    });

    it('encodes slot index as little-endian uint32 tail', () => {
        const aad = buildPageAad('/x', 0x11223344);
        const tail = aad.subarray(aad.length - 4);
        // little-endian 0x11223344 → bytes 44, 33, 22, 11
        expect(Array.from(tail)).toEqual([0x44, 0x33, 0x22, 0x11]);
    });

    it('differs for different paths at the same slot', () => {
        const a = buildPageAad('/db-a', 5);
        const b = buildPageAad('/db-b', 5);
        expect(Buffer.from(a).equals(Buffer.from(b))).toBe(false);
    });

    it('differs for different slots under the same path', () => {
        const a = buildPageAad('/db', 5);
        const b = buildPageAad('/db', 6);
        expect(Buffer.from(a).equals(Buffer.from(b))).toBe(false);
    });
});

describe('key-registry', () => {
    it('roundtrips register / get', () => {
        const path = '/t1.db';
        const key = makeKey(42);
        registerKeyForPath(path, key);
        expect(isPathEncrypted(path)).toBe(true);
        expect(getKeyForPath(path)).toBe(key);
        clearKeyForPath(path);
    });

    it('clear wipes the buffer and removes the entry', () => {
        const path = '/t2.db';
        const key = makeKey(7);
        const ref = key; // same backing array
        registerKeyForPath(path, key);
        clearKeyForPath(path);
        // Buffer zeroed in place.
        expect(Array.from(ref)).toEqual(new Array(32).fill(0));
        expect(isPathEncrypted(path)).toBe(false);
        expect(getKeyForPath(path)).toBeUndefined();
    });

    it('clearing an unregistered path is a no-op', () => {
        expect(() => clearKeyForPath('/never-registered.db')).not.toThrow();
    });
});

describe('page envelope round-trip', () => {
    it('encrypt then decrypt recovers a full 4096-byte plaintext page', () => {
        const key = makeKey(1);
        const aad = buildPageAad('/round.db', 42);

        const plaintext = new Uint8Array(PAGE_PLAINTEXT_LEN);
        for (let i = 0; i < plaintext.length; i++) plaintext[i] = (i * 7) & 0xff;

        const enc = encryptChaCha20Poly1305(plaintext, key, aad);
        // ciphertext carries tag at the tail (ChaCha20-Poly1305 convention).
        expect(enc.nonce.length).toBe(PAGE_NONCE_LEN);
        expect(enc.ciphertext.length).toBe(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);

        // Simulate the on-disk physical slot: [cipher(4096) | nonce(12) | tag(16)]
        const slot = new Uint8Array(PHYSICAL_SLOT_SIZE);
        slot.set(enc.ciphertext.subarray(0, PAGE_PLAINTEXT_LEN), 0);
        slot.set(enc.nonce, PAGE_PLAINTEXT_LEN);
        slot.set(
            enc.ciphertext.subarray(PAGE_PLAINTEXT_LEN),
            PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
        );

        // Reverse: split and decrypt.
        const onDiskCipher = slot.subarray(0, PAGE_PLAINTEXT_LEN);
        const onDiskNonce = slot.subarray(
            PAGE_PLAINTEXT_LEN,
            PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
        );
        const onDiskTag = slot.subarray(PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN);
        const cipherPlusTag = new Uint8Array(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);
        cipherPlusTag.set(onDiskCipher, 0);
        cipherPlusTag.set(onDiskTag, PAGE_PLAINTEXT_LEN);

        const decrypted = decryptChaCha20Poly1305(
            { ciphertext: cipherPlusTag, nonce: onDiskNonce },
            key,
            aad
        );
        expect(decrypted.length).toBe(PAGE_PLAINTEXT_LEN);
        expect(Buffer.from(decrypted).equals(Buffer.from(plaintext))).toBe(true);
    });

    it('decrypt fails with the wrong key', () => {
        const plaintext = new Uint8Array(PAGE_PLAINTEXT_LEN).fill(0xab);
        const aad = buildPageAad('/x', 0);
        const enc = encryptChaCha20Poly1305(plaintext, makeKey(1), aad);
        expect(() =>
            decryptChaCha20Poly1305(enc, makeKey(2), aad)
        ).toThrow();
    });

    it('decrypt fails with the wrong AAD (cross-DB swap detection)', () => {
        const plaintext = new Uint8Array(PAGE_PLAINTEXT_LEN).fill(0x5c);
        const aadA = buildPageAad('/db-a', 5);
        const aadB = buildPageAad('/db-b', 5);
        const key = makeKey(99);
        const enc = encryptChaCha20Poly1305(plaintext, key, aadA);
        // Same key, same slotIndex, different dbPath → auth failure.
        expect(() =>
            decryptChaCha20Poly1305(enc, key, aadB)
        ).toThrow();
    });

    it('decrypt fails when any ciphertext byte is flipped (tamper detection)', () => {
        const plaintext = new Uint8Array(PAGE_PLAINTEXT_LEN).fill(0x33);
        const aad = buildPageAad('/x', 0);
        const key = makeKey(5);
        const enc = encryptChaCha20Poly1305(plaintext, key, aad);
        // Flip one byte of the ciphertext.
        const tamperedCipher = new Uint8Array(enc.ciphertext);
        tamperedCipher[100] ^= 0x01;
        expect(() =>
            decryptChaCha20Poly1305(
                { ciphertext: tamperedCipher, nonce: enc.nonce },
                key,
                aad
            )
        ).toThrow();
    });

    it('two encryptions of identical plaintext produce different ciphertexts (random nonce)', () => {
        const plaintext = new Uint8Array(PAGE_PLAINTEXT_LEN).fill(0x7a);
        const aad = buildPageAad('/x', 0);
        const key = makeKey(11);
        const a = encryptChaCha20Poly1305(plaintext, key, aad);
        const b = encryptChaCha20Poly1305(plaintext, key, aad);
        expect(Buffer.from(a.nonce).equals(Buffer.from(b.nonce))).toBe(false);
        expect(Buffer.from(a.ciphertext).equals(Buffer.from(b.ciphertext))).toBe(false);
    });
});

describe('physical slot layout (offset-remap)', () => {
    it('two adjacent slots occupy 2 * 4124 physical bytes and decrypt independently', () => {
        const key = makeKey(3);
        const path = '/span.db';
        const slotA = new Uint8Array(PAGE_PLAINTEXT_LEN);
        const slotB = new Uint8Array(PAGE_PLAINTEXT_LEN);
        for (let i = 0; i < PAGE_PLAINTEXT_LEN; i++) {
            slotA[i] = (i * 3) & 0xff;
            slotB[i] = (i * 5 + 1) & 0xff;
        }

        const encA = encryptChaCha20Poly1305(slotA, key, buildPageAad(path, 0));
        const encB = encryptChaCha20Poly1305(slotB, key, buildPageAad(path, 1));

        // Assemble the on-disk region: [slotA physical(4124)][slotB physical(4124)]
        const disk = new Uint8Array(PHYSICAL_SLOT_SIZE * 2);
        const writePhysical = (enc: typeof encA, slotIndex: number) => {
            const base = slotIndex * PHYSICAL_SLOT_SIZE;
            disk.set(enc.ciphertext.subarray(0, PAGE_PLAINTEXT_LEN), base);
            disk.set(enc.nonce, base + PAGE_PLAINTEXT_LEN);
            disk.set(
                enc.ciphertext.subarray(PAGE_PLAINTEXT_LEN),
                base + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
            );
        };
        writePhysical(encA, 0);
        writePhysical(encB, 1);

        // Decrypt slot B using its own slice of the disk — proves the slots
        // are independent under the offset remap and that the AAD's slotIndex
        // binds the ciphertext to its own position (swapping would auth-fail).
        const readPhysical = (slotIndex: number) => {
            const base = slotIndex * PHYSICAL_SLOT_SIZE;
            const ct = disk.subarray(base, base + PAGE_PLAINTEXT_LEN);
            const nonce = disk.subarray(
                base + PAGE_PLAINTEXT_LEN,
                base + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
            );
            const tag = disk.subarray(
                base + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN,
                base + PHYSICAL_SLOT_SIZE
            );
            const cpt = new Uint8Array(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);
            cpt.set(ct, 0);
            cpt.set(tag, PAGE_PLAINTEXT_LEN);
            return { cipherPlusTag: cpt, nonce };
        };

        const aBack = readPhysical(0);
        const bBack = readPhysical(1);
        expect(
            Buffer.from(
                decryptChaCha20Poly1305(
                    { ciphertext: aBack.cipherPlusTag, nonce: aBack.nonce },
                    key,
                    buildPageAad(path, 0)
                )
            ).equals(Buffer.from(slotA))
        ).toBe(true);
        expect(
            Buffer.from(
                decryptChaCha20Poly1305(
                    { ciphertext: bBack.cipherPlusTag, nonce: bBack.nonce },
                    key,
                    buildPageAad(path, 1)
                )
            ).equals(Buffer.from(slotB))
        ).toBe(true);

        // Slot-swap must fail AEAD (wrong AAD slotIndex).
        expect(() =>
            decryptChaCha20Poly1305(
                { ciphertext: aBack.cipherPlusTag, nonce: aBack.nonce },
                key,
                buildPageAad(path, 1)
            )
        ).toThrow();
    });
});

