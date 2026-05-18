// Streaming import path for the asymmetric encrypted-disk envelope (v3).
//
// Consumes a Blob holding the full MessagePack `EncryptedDiskEnvelope` via
// blob.stream() — never materialises the whole envelope as a single buffer
// in the worker. Two passes:
//
//   1. Preflight: walk the file table, AEAD-decrypt slot 0 of each file
//      under K_wrap with `prf-vfs-v1|{dbPath}|0` AAD. Tag failure means
//      WRONG_KEY; abort with no writes done.
//   2. Commit:    wipe the SAH pool, re-stream, accumulate one rekeyed
//      buffer per file, hand each to `poolUtil.importDb(dbPath, bytes,
//      opaque=true)`.
//
// Why two passes: preflight preserves the wipe-after-validate invariant
// the legacy byte[] import had. blob.stream() is re-callable on the same
// Blob, so we re-open between passes — the bytes never live concatenated
// in JS heap.

import {
    encryptChaCha20Poly1305,
    decryptChaCha20Poly1305,
    clearBytes,
} from '@sqlitewasmblazor/crypto-core';
import { buildPageAad } from './aad.js';
import {
    BufferedStreamReader,
    readArrayHeader,
    readBinHeader,
    readStr,
    readUint,
} from '../../bridge/msgpack-stream.js';

const SECTOR_SIZE = 4096;
const PAGE_NONCE_LEN = 12;
const PAGE_TAG_LEN = 16;
const PHYSICAL_SLOT_SIZE = SECTOR_SIZE + PAGE_NONCE_LEN + PAGE_TAG_LEN; // 4124

const ENVELOPE_VERSION = 3;
const ENVELOPE_ARRAY_LEN = 8;
const ENVELOPE_AAD_VERSION = 'v1';
const PRF_SALT_LEN = 32;

/**
 * Mirrors the C# <c>DiskImportResult</c> enum so callers can branch on the
 * Promise resolution without crossing string boundaries.
 */
export const DiskImportResult = Object.freeze({
    OK: 0,
    WRONG_KEY: 1,
    EXISTING_DB_REFUSED: 2,
} as const);

export type DiskImportResultCode = typeof DiskImportResult[keyof typeof DiskImportResult];

interface PoolUtilLike {
    listDatabases(): string[];
    importDb(path: string, data: Uint8Array, opaque?: boolean): unknown;
}

/**
 * Forward-skip past the envelope's leading metadata block (everything
 * before <c>Files</c>) on a fresh stream. After this call the reader is
 * positioned exactly at the <c>Files</c> array header.
 *
 * <c>extract</c> = true captures the metadata into the returned struct
 * (used by the preflight pass to verify version + collect inputs). When
 * false the metadata is consumed and discarded (used by the commit pass
 * which re-derives nothing from it).
 */
async function consumeEnvelopeMetadata(
    reader: BufferedStreamReader,
    extract: boolean,
): Promise<{
    version: number;
    aadVersion: string;
    prfSaltLen: number;
} | undefined> {
    const arrLen = await readArrayHeader(reader);
    if (arrLen !== ENVELOPE_ARRAY_LEN) {
        throw new Error(
            `importDiskStreamed: expected envelope array(${ENVELOPE_ARRAY_LEN}), got array(${arrLen})`);
    }
    const version = await readUint(reader);
    if (version !== ENVELOPE_VERSION) {
        throw new Error(
            `importDiskStreamed: unsupported envelope Version=${version} (expected ${ENVELOPE_VERSION})`);
    }
    const aadVersion = await readStr(reader);
    if (aadVersion !== ENVELOPE_AAD_VERSION) {
        throw new Error(
            `importDiskStreamed: unsupported AadVersion='${aadVersion}' (expected '${ENVELOPE_AAD_VERSION}')`);
    }
    const prfSaltLen = await readBinHeader(reader);
    if (prfSaltLen !== PRF_SALT_LEN) {
        throw new Error(
            `importDiskStreamed: PrfSalt must be ${PRF_SALT_LEN} bytes, got ${prfSaltLen}`);
    }
    await reader.skip(prfSaltLen);
    // Discard remaining metadata strings: EphPub, WrapCt, WrapNonce,
    // CredIdHint. Caller already supplies the unwrapped K_wrap; these
    // strings are envelope-self-describing fields for future cross-app
    // import paths.
    await readStr(reader);
    await readStr(reader);
    await readStr(reader);
    await readStr(reader);
    return extract ? { version, aadVersion, prfSaltLen } : undefined;
}

/**
 * Lift the per-slot ciphertext layout (`ct(4096) ‖ nonce(12) ‖ tag(16)`)
 * into the form `decryptChaCha20Poly1305` accepts (`ct‖tag` + separate
 * nonce). Returns the freshly decrypted plaintext; caller wipes it.
 */
function decryptSlot(slot: Uint8Array, key: Uint8Array, aad: Uint8Array): Uint8Array {
    const ct = slot.subarray(0, SECTOR_SIZE);
    const nonce = slot.subarray(SECTOR_SIZE, SECTOR_SIZE + PAGE_NONCE_LEN);
    const tag = slot.subarray(SECTOR_SIZE + PAGE_NONCE_LEN, PHYSICAL_SLOT_SIZE);
    const cipherPlusTag = new Uint8Array(SECTOR_SIZE + PAGE_TAG_LEN);
    cipherPlusTag.set(ct, 0);
    cipherPlusTag.set(tag, SECTOR_SIZE);
    return decryptChaCha20Poly1305({ ciphertext: cipherPlusTag, nonce }, key, aad);
}

/**
 * Inverse of <see cref="decryptSlot"/>: write a freshly AEAD-sealed slot
 * (`ct(4096) ‖ nonce(12) ‖ tag(16)`) at <paramref name="dst"/> position.
 */
function writeEncryptedSlot(
    plaintext: Uint8Array,
    key: Uint8Array,
    aad: Uint8Array,
    out: Uint8Array,
    dstStart: number,
): void {
    const enc = encryptChaCha20Poly1305(plaintext, key, aad);
    out.set(enc.ciphertext.subarray(0, SECTOR_SIZE), dstStart);
    out.set(enc.nonce, dstStart + SECTOR_SIZE);
    out.set(enc.ciphertext.subarray(SECTOR_SIZE), dstStart + SECTOR_SIZE + PAGE_NONCE_LEN);
}

/**
 * Pass 1 — preflight. Walks the envelope's Files array, AEAD-verifies
 * slot 0 of each file under K_wrap. Returns <c>OK</c> if every file's
 * slot 0 authenticates; <c>WRONG_KEY</c> on the first tag failure (no
 * writes happen in this pass). Caller (C# service) must hold off any
 * pool-mutating operation until this returns <c>OK</c>.
 */
export async function importDiskStreamPreflight(
    blob: Blob,
    kWrap: Uint8Array,
): Promise<DiskImportResultCode> {
    const reader = new BufferedStreamReader(blob.stream().getReader());
    try {
        await consumeEnvelopeMetadata(reader, false);
        const fileCount = await readArrayHeader(reader);
        for (let i = 0; i < fileCount; i++) {
            const tupleLen = await readArrayHeader(reader);
            if (tupleLen !== 2) {
                throw new Error(
                    `importDiskStreamed[preflight]: EncryptedDiskFile must be array(2), got array(${tupleLen})`);
            }
            const name = await readStr(reader);
            const binLen = await readBinHeader(reader);
            if (binLen === 0 || binLen % PHYSICAL_SLOT_SIZE !== 0) {
                throw new Error(
                    `importDiskStreamed[preflight]: file '${name}' length ${binLen} is not a positive multiple of slot size ${PHYSICAL_SLOT_SIZE}`);
            }
            const slot0 = await reader.read(PHYSICAL_SLOT_SIZE);
            const dbPath = `/databases/${name}`;
            const aad = buildPageAad(dbPath, 0);
            try {
                const plaintext = decryptSlot(slot0, kWrap, aad);
                clearBytes(plaintext);
            } catch {
                return DiskImportResult.WRONG_KEY;
            } finally {
                clearBytes(slot0);
            }
            // Discard slots 1..N-1 of this file (preflight only checks
            // slot 0; tag-correctness on slot 0 is sufficient evidence the
            // K_wrap matches the file's encryption key — the file's
            // remaining slots share the same key by construction).
            await reader.skip(binLen - PHYSICAL_SLOT_SIZE);
        }
        return DiskImportResult.OK;
    } finally {
        reader.releaseLock();
    }
}

/**
 * Pass 2 — commit. Re-streams the envelope and for each file decrypts
 * every slot under K_wrap + re-encrypts under <paramref name="globalKey"/>,
 * then hands the rekeyed buffer to <c>poolUtil.importDb</c>. Per-file
 * rekey output is dropped (set to empty array reference) after import
 * so the GC reclaims it before the next file's buffer is allocated.
 *
 * Caller (C# service) must have already wiped the pool and registered
 * <paramref name="globalKey"/> as the worker's globalKey before calling
 * this — typically via <c>WipePoolAsync</c> + <c>EnterEncryptedAsync</c>.
 */
export async function importDiskStreamCommit(
    blob: Blob,
    kWrap: Uint8Array,
    globalKey: Uint8Array,
    poolUtil: PoolUtilLike,
): Promise<void> {
    const reader = new BufferedStreamReader(blob.stream().getReader());
    try {
        await consumeEnvelopeMetadata(reader, false);
        const fileCount = await readArrayHeader(reader);
        for (let i = 0; i < fileCount; i++) {
            const tupleLen = await readArrayHeader(reader);
            if (tupleLen !== 2) {
                throw new Error(
                    `importDiskStreamed[commit]: EncryptedDiskFile must be array(2), got array(${tupleLen})`);
            }
            const name = await readStr(reader);
            const binLen = await readBinHeader(reader);
            const dbPath = `/databases/${name}`;
            const slotCount = binLen / PHYSICAL_SLOT_SIZE;
            let rekeyed: Uint8Array | null = new Uint8Array(binLen);
            try {
                for (let s = 0; s < slotCount; s++) {
                    const slot = await reader.read(PHYSICAL_SLOT_SIZE);
                    const aad = buildPageAad(dbPath, s);
                    const plaintext = decryptSlot(slot, kWrap, aad);
                    try {
                        writeEncryptedSlot(plaintext, globalKey, aad, rekeyed, s * PHYSICAL_SLOT_SIZE);
                    } finally {
                        clearBytes(plaintext);
                        clearBytes(slot);
                    }
                }
                poolUtil.importDb(dbPath, rekeyed, true);
            } finally {
                // Drop the per-file buffer ref so V8 can reclaim it
                // before allocating the next file's buffer. Bytes are
                // already ciphertext under globalKey so not secret-bearing.
                rekeyed = null;
            }
        }
    } finally {
        reader.releaseLock();
    }
}

