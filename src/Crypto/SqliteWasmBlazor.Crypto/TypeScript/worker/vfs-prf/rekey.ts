// Slot rekey primitive for the PRF-keyed VFS.
//
// Reads bytes returned by `poolUtil.exportFile(dbPath)` and emits a new buffer
// where every 4096-byte logical SQLite page is re-wrapped under a different
// (or absent) key.
//
// Source/target combos:
//   sourceKey === undefined  → input is plain SQLite pages (4096 B each)
//   sourceKey: Uint8Array    → input is physical encrypted slots (4124 B each)
//   targetKey === undefined  → output is plain SQLite pages (4096 B each)
//   targetKey: Uint8Array    → output is physical encrypted slots (4124 B each)
//
// AAD is `prf-vfs-v1|{dbPath}|{slotIndex}` for both decrypt and re-encrypt —
// the recipient must import to the same dbPath the sender exported from.

import {
    encryptChaCha20Poly1305,
    decryptChaCha20Poly1305,
    clearBytes,
} from '@sqlitewasmblazor/crypto-core';
import { buildPageAad } from './aad.js';

const SECTOR_SIZE = 4096;
const PAGE_NONCE_LEN = 12;
const PAGE_TAG_LEN = 16;
const PAGE_PLAINTEXT_LEN = SECTOR_SIZE;
const PHYSICAL_SLOT_SIZE = SECTOR_SIZE + PAGE_NONCE_LEN + PAGE_TAG_LEN; // 4124

/**
 * Diagnostic-only key marker. Mirrors the helper in sqlite-worker.ts without
 * emitting even a prefix of secret key material.
 */
function keyFingerprint(key: Uint8Array | undefined): string {
    if (key === undefined) return '<plain>';
    return `<redacted:${key.length}B>`;
}

export function rekeySlots(
    bytesIn: Uint8Array,
    dbPath: string,
    sourceKey: Uint8Array | undefined,
    targetKey: Uint8Array | undefined,
): Uint8Array {
    const sourceSlotSize = sourceKey === undefined ? SECTOR_SIZE : PHYSICAL_SLOT_SIZE;
    const targetSlotSize = targetKey === undefined ? SECTOR_SIZE : PHYSICAL_SLOT_SIZE;

    if (bytesIn.length === 0) {
        return new Uint8Array(0);
    }
    if (bytesIn.length % sourceSlotSize !== 0) {
        throw new Error(
            `rekeySlots: input length ${bytesIn.length} is not a multiple of source slot size ${sourceSlotSize}`,
        );
    }

    const slotCount = bytesIn.length / sourceSlotSize;
    const out = new Uint8Array(slotCount * targetSlotSize);

    console.log(
        `[rekeySlots] dbPath=${dbPath} ` +
        `sourceKey=${keyFingerprint(sourceKey)} ` +
        `targetKey=${keyFingerprint(targetKey)} ` +
        `slots=${slotCount} (sourceSlot=${sourceSlotSize} → targetSlot=${targetSlotSize})`);

    for (let i = 0; i < slotCount; i++) {
        const srcStart = i * sourceSlotSize;
        const aad = buildPageAad(dbPath, i);
        if (i === 0) {
            // AAD = "prf-vfs-v1|{dbPath}|" + LE-uint32(slotIndex). Decode
            // the prefix back to a string so two log lines from sender
            // and recipient can be diffed at a glance.
            const aadPrefixLen = aad.length - 4;
            const aadPrefix = new TextDecoder().decode(aad.subarray(0, aadPrefixLen));
            const aadIdxLE = Array.from(aad.subarray(aadPrefixLen))
                .map(b => b.toString(16).padStart(2, '0')).join('');
            const slotHead = Array.from(bytesIn.subarray(srcStart, srcStart + 8))
                .map(b => b.toString(16).padStart(2, '0')).join('');
            console.log(
                `[rekeySlots] slot[0] aad.prefix="${aadPrefix}" aad.idxLE=${aadIdxLE} ` +
                `aad.totalLen=${aad.length} sourceSlot[0..8]=${slotHead}`);
        }

        // When sourceKey is undefined the plaintext is a Uint8Array view
        // INTO bytesIn (no fresh allocation, callers own the lifetime).
        // When sourceKey is defined the plaintext is a fresh allocation
        // returned by decryptChaCha20Poly1305 — that copy is real secret
        // material and must be wiped after the slot's encrypt/copy step.
        // Per-slot try/finally so an encrypt failure mid-loop still wipes
        // the slot's plaintext.
        let plaintext: Uint8Array;
        const ownsPlaintext = sourceKey !== undefined;
        if (sourceKey === undefined) {
            plaintext = bytesIn.subarray(srcStart, srcStart + SECTOR_SIZE);
        } else {
            const ciphertext = bytesIn.subarray(srcStart, srcStart + PAGE_PLAINTEXT_LEN);
            const nonce = bytesIn.subarray(
                srcStart + PAGE_PLAINTEXT_LEN,
                srcStart + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN,
            );
            const tag = bytesIn.subarray(
                srcStart + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN,
                srcStart + PHYSICAL_SLOT_SIZE,
            );
            const cipherPlusTag = new Uint8Array(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);
            cipherPlusTag.set(ciphertext, 0);
            cipherPlusTag.set(tag, PAGE_PLAINTEXT_LEN);
            plaintext = decryptChaCha20Poly1305(
                { ciphertext: cipherPlusTag, nonce },
                sourceKey,
                aad,
            );
        }

        try {
            const dstStart = i * targetSlotSize;
            if (targetKey === undefined) {
                out.set(plaintext, dstStart);
            } else {
                const enc = encryptChaCha20Poly1305(plaintext, targetKey, aad);
                // enc.ciphertext = ciphertext(4096) || tag(16) — length 4112.
                out.set(enc.ciphertext.subarray(0, PAGE_PLAINTEXT_LEN), dstStart);
                out.set(enc.nonce, dstStart + PAGE_PLAINTEXT_LEN);
                out.set(
                    enc.ciphertext.subarray(PAGE_PLAINTEXT_LEN),
                    dstStart + PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN,
                );
            }
        } finally {
            if (ownsPlaintext) {
                clearBytes(plaintext);
            }
        }
    }

    return out;
}
