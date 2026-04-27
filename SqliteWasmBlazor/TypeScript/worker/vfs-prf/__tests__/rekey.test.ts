// Tests for the slot-rekey primitive that drives ExportDatabaseAsync
// (mode=PLAIN / mode=REKEY) and (future) RotateVfsKeyAsync.
//
// Scope: pure helper. We synthesize input buffers in the same shapes the
// worker would produce via poolUtil.exportFile() (4096-byte plain pages or
// 4124-byte physical slots) and verify all four source/target combos.

import { describe, it, expect } from 'vitest';
import {
    encryptChaCha20Poly1305,
    decryptChaCha20Poly1305,
} from '@sqlitewasmblazor/crypto-core';
import { rekeySlots } from '../rekey.js';
import { buildPageAad } from '../aad.js';

const SECTOR_SIZE = 4096;
const PAGE_NONCE_LEN = 12;
const PAGE_TAG_LEN = 16;
const PAGE_PLAINTEXT_LEN = SECTOR_SIZE;
const PHYSICAL_SLOT_SIZE = SECTOR_SIZE + PAGE_NONCE_LEN + PAGE_TAG_LEN; // 4124

function makeKey(seed: number): Uint8Array {
    const k = new Uint8Array(32);
    for (let i = 0; i < 32; i++) k[i] = (seed + i) & 0xff;
    return k;
}

function makeSlotPlaintext(seed: number, slotIndex: number): Uint8Array {
    const p = new Uint8Array(PAGE_PLAINTEXT_LEN);
    for (let i = 0; i < p.length; i++) {
        p[i] = (seed * 31 + slotIndex * 13 + i * 7) & 0xff;
    }
    return p;
}

// Build a synthetic encrypted-DB exportFile buffer: N physical slots laid
// out [ciphertext(4096) | nonce(12) | tag(16)] each, encrypted under key
// with AAD = buildPageAad(dbPath, slotIndex).
function buildEncryptedExport(
    plaintexts: Uint8Array[],
    key: Uint8Array,
    dbPath: string,
): Uint8Array {
    const out = new Uint8Array(plaintexts.length * PHYSICAL_SLOT_SIZE);
    for (let i = 0; i < plaintexts.length; i++) {
        const enc = encryptChaCha20Poly1305(plaintexts[i], key, buildPageAad(dbPath, i));
        const base = i * PHYSICAL_SLOT_SIZE;
        out.set(enc.ciphertext.subarray(0, PAGE_PLAINTEXT_LEN), base);
        out.set(enc.nonce, base + PAGE_PLAINTEXT_LEN);
        out.set(
            enc.ciphertext.subarray(PAGE_PLAINTEXT_LEN),
            base + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN,
        );
    }
    return out;
}

// Decrypt a synthetic encrypted-export buffer back into plaintext slots.
function decryptEncryptedExport(
    encrypted: Uint8Array,
    key: Uint8Array,
    dbPath: string,
): Uint8Array[] {
    if (encrypted.length % PHYSICAL_SLOT_SIZE !== 0) {
        throw new Error(`encrypted length ${encrypted.length} not slot-aligned`);
    }
    const slotCount = encrypted.length / PHYSICAL_SLOT_SIZE;
    const out: Uint8Array[] = [];
    for (let i = 0; i < slotCount; i++) {
        const base = i * PHYSICAL_SLOT_SIZE;
        const ct = encrypted.subarray(base, base + PAGE_PLAINTEXT_LEN);
        const nonce = encrypted.subarray(
            base + PAGE_PLAINTEXT_LEN,
            base + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN,
        );
        const tag = encrypted.subarray(
            base + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN,
            base + PHYSICAL_SLOT_SIZE,
        );
        const cpt = new Uint8Array(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);
        cpt.set(ct, 0);
        cpt.set(tag, PAGE_PLAINTEXT_LEN);
        out.push(
            decryptChaCha20Poly1305(
                { ciphertext: cpt, nonce },
                key,
                buildPageAad(dbPath, i),
            ),
        );
    }
    return out;
}

describe('rekeySlots — four source/target combos', () => {
    const dbPath = '/databases/rekey.db';
    const slotCount = 4;
    const plaintexts = Array.from({ length: slotCount }, (_, i) => makeSlotPlaintext(123, i));

    it('plain → plain is the identity (verbatim copy)', () => {
        const input = new Uint8Array(slotCount * SECTOR_SIZE);
        plaintexts.forEach((p, i) => input.set(p, i * SECTOR_SIZE));

        const out = rekeySlots(input, dbPath, undefined, undefined);

        expect(out.length).toBe(input.length);
        expect(Buffer.from(out).equals(Buffer.from(input))).toBe(true);
    });

    it('plain → encrypted under K_new produces slots that decrypt back', () => {
        const input = new Uint8Array(slotCount * SECTOR_SIZE);
        plaintexts.forEach((p, i) => input.set(p, i * SECTOR_SIZE));

        const kNew = makeKey(7);
        const out = rekeySlots(input, dbPath, undefined, kNew);

        expect(out.length).toBe(slotCount * PHYSICAL_SLOT_SIZE);

        const recovered = decryptEncryptedExport(out, kNew, dbPath);
        expect(recovered).toHaveLength(slotCount);
        for (let i = 0; i < slotCount; i++) {
            expect(Buffer.from(recovered[i]).equals(Buffer.from(plaintexts[i]))).toBe(true);
        }
    });

    it('encrypted under K_old → plain recovers the original plaintext', () => {
        const kOld = makeKey(11);
        const input = buildEncryptedExport(plaintexts, kOld, dbPath);

        const out = rekeySlots(input, dbPath, kOld, undefined);

        expect(out.length).toBe(slotCount * SECTOR_SIZE);
        for (let i = 0; i < slotCount; i++) {
            const slice = out.subarray(i * SECTOR_SIZE, (i + 1) * SECTOR_SIZE);
            expect(Buffer.from(slice).equals(Buffer.from(plaintexts[i]))).toBe(true);
        }
    });

    it('encrypted under K_old → encrypted under K_new round-trips', () => {
        const kOld = makeKey(13);
        const kNew = makeKey(17);
        const input = buildEncryptedExport(plaintexts, kOld, dbPath);

        const out = rekeySlots(input, dbPath, kOld, kNew);

        expect(out.length).toBe(slotCount * PHYSICAL_SLOT_SIZE);

        const recovered = decryptEncryptedExport(out, kNew, dbPath);
        expect(recovered).toHaveLength(slotCount);
        for (let i = 0; i < slotCount; i++) {
            expect(Buffer.from(recovered[i]).equals(Buffer.from(plaintexts[i]))).toBe(true);
        }
    });
});

describe('rekeySlots — AAD safety', () => {
    const slotCount = 3;
    const plaintexts = Array.from({ length: slotCount }, (_, i) => makeSlotPlaintext(42, i));

    it('rekeyed output decrypts only under the matching dbPath AAD', () => {
        const dbPathA = '/databases/sender.db';
        const dbPathB = '/databases/recipient.db';
        const kOld = makeKey(2);
        const kNew = makeKey(3);

        const input = buildEncryptedExport(plaintexts, kOld, dbPathA);
        const out = rekeySlots(input, dbPathA, kOld, kNew);

        // The recipient must reuse dbPathA in their AAD or AEAD will fail.
        // This is the documented constraint (cross-path migration deferred).
        expect(() => decryptEncryptedExport(out, kNew, dbPathB)).toThrow();
        // Sanity: matching path works.
        expect(() => decryptEncryptedExport(out, kNew, dbPathA)).not.toThrow();
    });

    it('decrypt with wrong K_new fails AEAD', () => {
        const dbPath = '/databases/wrong-key.db';
        const kOld = makeKey(4);
        const kNew = makeKey(5);
        const kWrong = makeKey(6);

        const input = buildEncryptedExport(plaintexts, kOld, dbPath);
        const out = rekeySlots(input, dbPath, kOld, kNew);

        expect(() => decryptEncryptedExport(out, kWrong, dbPath)).toThrow();
    });

    it('rekeying with wrong source key fails on the first slot (decrypt step)', () => {
        const dbPath = '/databases/bad-source.db';
        const kRight = makeKey(8);
        const kWrong = makeKey(9);
        const input = buildEncryptedExport(plaintexts, kRight, dbPath);

        expect(() =>
            rekeySlots(input, dbPath, kWrong, makeKey(10)),
        ).toThrow();
    });
});

describe('rekeySlots — input validation', () => {
    it('empty input returns empty output', () => {
        const out = rekeySlots(new Uint8Array(0), '/x', undefined, undefined);
        expect(out.length).toBe(0);
    });

    it('throws when input length is not a multiple of source slot size (plain source)', () => {
        const bad = new Uint8Array(SECTOR_SIZE + 7);
        expect(() => rekeySlots(bad, '/x', undefined, undefined)).toThrow(/not a multiple/);
    });

    it('throws when input length is not a multiple of source slot size (encrypted source)', () => {
        const bad = new Uint8Array(PHYSICAL_SLOT_SIZE + 5);
        expect(() => rekeySlots(bad, '/x', makeKey(1), undefined)).toThrow(/not a multiple/);
    });
});
