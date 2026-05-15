// Worker-side key state for the PRF-keyed VFS — single-key model.
//
// Mental model: the encrypted VFS is one disk. The disk is in one of three
// states — Plain, Encrypted+Locked, Encrypted+Unlocked. The single
// `globalKey` field below is the only crypto state in the worker:
//
//   - undefined  ⇒ disk is Plain OR Encrypted+Locked
//   - 32-byte K  ⇒ disk is Encrypted+Unlocked, every page I/O uses K
//
// C# orchestrates the lifecycle (SetEncryptionKeyAsync at unlock /
// ClearEncryptionKeyAsync at lock). The VFS hot path (xRead / xWrite)
// reads `globalKey` *dynamically* per page I/O — there is no per-OFile
// snapshot, so a key swap takes effect on every open file immediately
// without closing them.

import { clearBytes } from '@sqlitewasmblazor/crypto-core';

let globalKey: Uint8Array | undefined;

/**
 * Install the worker's global encryption key. Idempotent — wipes any
 * previously-installed buffer in place before storing the new one. Buffer
 * ownership transfers to the registry.
 */
export function setGlobalKey(key: Uint8Array): void {
    if (key.length !== 32) {
        clearBytes(key);
        throw new Error(`globalKey must be 32 bytes, got ${key.length}`);
    }
    if (globalKey !== undefined && globalKey !== key) {
        clearBytes(globalKey);
    }
    globalKey = key;
}

/**
 * Wipe the worker's global encryption key. Idempotent.
 */
export function clearGlobalKey(): void {
    if (globalKey !== undefined) {
        clearBytes(globalKey);
        globalKey = undefined;
    }
}

/**
 * True when the disk is currently Encrypted+Unlocked — i.e. xRead / xWrite
 * route through the encrypted hot path. Plain and Encrypted+Locked both
 * report false.
 */
export function hasGlobalKey(): boolean {
    return globalKey !== undefined;
}

/**
 * The currently-installed disk key, or undefined when the disk is not
 * Encrypted+Unlocked. Hot-path callers (xRead / xWrite) read this once per
 * top-level operation and pass the returned reference into the slot loop.
 *
 * Returns the live registry buffer — do NOT free or mutate it. For the
 * snapshot semantics needed by export/decrypt-in-place, use
 * {@link snapshotGlobalKey} which returns a fresh copy.
 */
export function getGlobalKey(): Uint8Array | undefined {
    return globalKey;
}

/**
 * Fresh copy of the current disk key for callers that need to retain it
 * past a possible swap (e.g. {@link decryptDb} reads with K_old after the
 * caller has rotated globalKey to K_new). Caller must {@link clearBytes}
 * the returned buffer when done.
 */
export function snapshotGlobalKey(): Uint8Array | undefined {
    if (globalKey === undefined) return undefined;
    return new Uint8Array(globalKey);
}
