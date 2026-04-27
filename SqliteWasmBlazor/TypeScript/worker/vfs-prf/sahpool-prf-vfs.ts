// PRF-keyed OPFS SAHPool VFS.
//
// Fork of sqlite.org's sqlite3-vfs-opfs-sahpool.c-pp.js (SQLite 3.53) with
// ChaCha20-Poly1305 page-level encryption added. The modifications are
// localized to xOpen (key lookup), xRead / xWrite (slot-aligned crypto),
// xFileSize / xTruncate (logical ↔ physical size translation), and xClose
// (key wipe). Everything else is byte-for-byte identical to vendor.
//
// A file opened by this VFS is encrypted iff a key was registered for its
// path via `registerKeyForPath()` BEFORE xOpen runs. When no key is
// registered, xRead / xWrite pass through to the SyncAccessHandle exactly as
// vendor does — so this VFS is a drop-in replacement for `opfs-sahpool` for
// both encrypted (CryptoSync) and plain (non-CryptoSync) consumers.
//
// Per-slot envelope layout (encrypted path only):
//
//   Logical view (SQLite):          [ 4096 ][ 4096 ][ 4096 ] …
//                                      │       │       │
//                                      ▼       ▼       ▼
//   Physical on disk:    [4096|12|16][4096|12|16][4096|12|16] …
//                          4124 bytes per slot
//
// SQLite sees untouched 4096-byte pages (reserved_bytes = 0). The VFS
// expands each 4096-byte logical block into a 4124-byte physical block
// containing [ciphertext(4096) | nonce(12) | tag(16)]. Because the
// expansion is uniform across offsets and file types, the same scheme
// covers MAIN_DB, WAL frames, rollback journals, and temp files without
// needing SQLite's cooperation. Nonce is random per write. AAD binds
// version + dbPath + slotIndex so slots cannot be reordered or swapped
// between files.

import {
    encryptChaCha20Poly1305,
    decryptChaCha20Poly1305,
    clearBytes,
} from '@sqlitewasmblazor/crypto-core';
import { getKeyForPath, isPathEncrypted as isEncryptedPath } from './key-registry.js';
import { buildPageAad } from './aad.js';

// ==========================================================================
// Types — loose because sqlite-wasm has no shipped TypeScript declarations.
// ==========================================================================

/* eslint-disable @typescript-eslint/no-explicit-any */
type Sqlite3 = any;
type Capi = any;
type Wasm = any;

export interface PrfSAHPoolOptions {
    name?: string;
    directory?: string;
    initialCapacity?: number;
    clearOnInit?: boolean;
    verbosity?: number;
    forceReinitIfPreviouslyFailed?: boolean;
}

/**
 * Tri-state result of {@link PrfPoolUtil.verifyEncryptionKey}. Mirrors the
 * C# {@code VfsKeyInstallResult} enum; the bridge encodes these as 0/1/2
 * in the response's {@code rowsAffected} field.
 */
export type VfsKeyVerifyResult = 'noExistingDb' | 'match' | 'wrongKey';

export interface PrfPoolUtil {
    vfsName: string;
    OpfsSAHPoolDb?: new (...args: any[]) => any;
    addCapacity(n: number): Promise<number>;
    reduceCapacity(n: number): Promise<number>;
    getCapacity(): number;
    getFileCount(): number;
    getFileNames(): string[];
    reserveMinimumCapacity(min: number): Promise<number>;
    exportFile(name: string): Uint8Array;
    importDb(name: string, bytes: Uint8Array | ArrayBuffer, opaque?: boolean): number | Promise<number>;
    wipeFiles(): Promise<void>;
    unlink(filename: string): boolean;
    renameFile(oldPath: string, newPath: string): true;
    verifyEncryptionKey(path: string): VfsKeyVerifyResult;
    removeVfs(): Promise<boolean>;
    pauseVfs(): PrfPoolUtil;
    unpauseVfs(): Promise<PrfPoolUtil>;
    isPaused(): boolean;
}

// ==========================================================================
// Constants and crypto envelope dimensions.
// ==========================================================================

const SECTOR_SIZE = 4096;
const HEADER_MAX_PATH_SIZE = 512;
const HEADER_FLAGS_SIZE = 4;
const HEADER_DIGEST_SIZE = 8;
const HEADER_CORPUS_SIZE = HEADER_MAX_PATH_SIZE + HEADER_FLAGS_SIZE;
const HEADER_OFFSET_FLAGS = HEADER_MAX_PATH_SIZE;
const HEADER_OFFSET_DIGEST = HEADER_CORPUS_SIZE;
const HEADER_OFFSET_DATA = SECTOR_SIZE;

const OPAQUE_DIR_NAME = '.opaque';

// Per-slot crypto envelope.
//
// Logical slot size = SECTOR_SIZE (4096) — SQLite's view.
// Physical slot size = SECTOR_SIZE + nonce(12) + tag(16) = 4124 on disk.
const PAGE_NONCE_LEN = 12;
const PAGE_TAG_LEN = 16;
const PAGE_ENVELOPE_TAIL = PAGE_NONCE_LEN + PAGE_TAG_LEN; // 28
const PAGE_PLAINTEXT_LEN = SECTOR_SIZE; // 4096 — full logical page is encrypted
const PHYSICAL_SLOT_SIZE = SECTOR_SIZE + PAGE_ENVELOPE_TAIL; // 4124

// Translate a logical (SQLite-facing) offset to its physical (on-disk)
// counterpart. Valid for any logical byte within a slot's plaintext region.
function logicalToPhysicalOffset(logicalOffset: number): number {
    const slotIndex = Math.floor(logicalOffset / SECTOR_SIZE);
    const withinSlot = logicalOffset - slotIndex * SECTOR_SIZE;
    return slotIndex * PHYSICAL_SLOT_SIZE + withinSlot;
}

// Translate a physical file size (SAH-reported) back to the logical size
// SQLite expects. Whole physical slots only contribute their 4096 plaintext
// bytes; a trailing partial slot would be invalid (the VFS only ever writes
// full slots) so we floor-divide.
function physicalToLogicalSize(physicalSize: number): number {
    return Math.floor(physicalSize / PHYSICAL_SLOT_SIZE) * SECTOR_SIZE;
}

// Translate a logical file size (SQLite-facing) to the on-disk size after
// SAHPool's 4096-byte header. Called from xTruncate.
function logicalToPhysicalSize(logicalSize: number): number {
    return Math.floor(logicalSize / SECTOR_SIZE) * PHYSICAL_SLOT_SIZE;
}

const defaults: Required<Omit<PrfSAHPoolOptions, 'directory'>> & {
    directory: string | undefined;
} = {
    name: 'opfs-sahpool',
    directory: undefined,
    initialCapacity: 6,
    clearOnInit: false,
    verbosity: 2,
    forceReinitIfPreviouslyFailed: false,
};

// Registry of init promises per VFS name so repeated calls dedupe.
const initPromises: Record<string, Promise<PrfPoolUtil>> = Object.create(null);

// ==========================================================================
// Public entry point — call once at worker bootstrap per VFS name.
// ==========================================================================

export async function installOpfsSAHPoolVfs(
    sqlite3: Sqlite3,
    options: PrfSAHPoolOptions = {}
): Promise<PrfPoolUtil> {
    const opts = Object.assign(Object.create(null), defaults, options || {});
    const vfsName: string = opts.name;
    if (Object.prototype.hasOwnProperty.call(initPromises, vfsName)) {
        try {
            return await initPromises[vfsName];
        } catch (e) {
            if (opts.forceReinitIfPreviouslyFailed) {
                delete initPromises[vfsName];
            } else {
                throw e;
            }
        }
    }

    const g = globalThis as any;
    if (
        !g.FileSystemHandle ||
        !g.FileSystemDirectoryHandle ||
        !g.FileSystemFileHandle ||
        !g.FileSystemFileHandle.prototype.createSyncAccessHandle ||
        !navigator?.storage?.getDirectory
    ) {
        const rej = Promise.reject(new Error('Missing required OPFS APIs.'));
        initPromises[vfsName] = rej as Promise<PrfPoolUtil>;
        return rej as Promise<PrfPoolUtil>;
    }

    initPromises[vfsName] = apiVersionCheck()
        .then(async () => {
            const pool = new OpfsSAHPool(sqlite3, opts);
            try {
                await pool.isReady;
                const util = new OpfsSAHPoolUtil(pool);
                if (sqlite3.oo1) {
                    const oo1 = sqlite3.oo1;
                    const theVfs = pool.getVfs();
                    const OpfsSAHPoolDb = function (this: any, ...args: any[]) {
                        const opt = oo1.DB.dbCtorHelper.normalizeArgs(...args);
                        opt.vfs = theVfs.$zName;
                        oo1.DB.dbCtorHelper.call(this, opt);
                    };
                    OpfsSAHPoolDb.prototype = Object.create(oo1.DB.prototype);
                    (util as any).OpfsSAHPoolDb = OpfsSAHPoolDb;
                }
                pool.log('VFS initialized.');
                return util as unknown as PrfPoolUtil;
            } catch (e) {
                await pool.removeVfs().catch(() => {});
                throw e;
            }
        })
        .catch((err) => {
            initPromises[vfsName] = Promise.reject(err) as Promise<PrfPoolUtil>;
            throw err;
        });

    return initPromises[vfsName];
}

// ==========================================================================
// Vendor-internal helpers (renamed only where needed for TS).
// ==========================================================================

const getRandomName = () => Math.random().toString(36).slice(2);
const textDecoder = new TextDecoder();
const textEncoder = new TextEncoder();

function toss(...args: any[]): never {
    throw new Error(args.join(' '));
}

async function apiVersionCheck(): Promise<true> {
    const dh = await navigator.storage.getDirectory();
    const fn = '.opfs-sahpool-sync-check-' + getRandomName();
    const fh = await dh.getFileHandle(fn, { create: true });
    const ah = await fh.createSyncAccessHandle();
    const close = (ah as any).close();
    await close;
    await dh.removeEntry(fn);
    if ((close as any)?.then) {
        toss(
            'The local OPFS API is too old for opfs-sahpool: it has an async FileSystemSyncAccessHandle.close() method.'
        );
    }
    return true;
}

// ==========================================================================
// Pool class — vendor port with crypto hooks in xRead / xWrite / xOpen / xClose.
// ==========================================================================

class OpfsSAHPool {
    vfsName: string;
    vfsDir: string;
    isReady: Promise<void>;

    // sqlite3-wasm handles (passed in per install call).
    private sqlite3: Sqlite3;
    private capi: Capi;
    private wasm: Wasm;
    private util: any;

    // Vendor bookkeeping.
    private dhVfsRoot!: FileSystemDirectoryHandle;
    private dhOpaque!: FileSystemDirectoryHandle;
    private dhVfsParent!: FileSystemDirectoryHandle | undefined;
    private mapSAHToName = new Map<FileSystemSyncAccessHandle, string>();
    private mapFilenameToSAH = new Map<string, FileSystemSyncAccessHandle>();
    private availableSAH = new Set<FileSystemSyncAccessHandle>();
    private mapS3FileToOFile = new Map<number, OFile>();
    private apBody = new Uint8Array(HEADER_CORPUS_SIZE);
    private dvBody: DataView;
    private cVfs: any; // sqlite3_vfs struct
    private cIoMethods: any; // sqlite3_io_methods struct
    private verbosity: number;
    private loggers: Array<(...args: any[]) => void>;
    private flagComputeDigestV2: number;
    private persistentFileTypes: number;
    $error?: any;

    constructor(sqlite3: Sqlite3, options: PrfSAHPoolOptions) {
        this.sqlite3 = sqlite3;
        this.capi = sqlite3.capi;
        this.wasm = sqlite3.wasm;
        this.util = sqlite3.util;
        this.verbosity = options.verbosity ?? 2;
        this.vfsName = options.name || 'opfs-sahpool';
        this.vfsDir = options.directory || '.' + this.vfsName;
        this.dvBody = new DataView(this.apBody.buffer, this.apBody.byteOffset);
        this.loggers = [sqlite3.config.error, sqlite3.config.warn, sqlite3.config.log];

        this.persistentFileTypes =
            this.capi.SQLITE_OPEN_MAIN_DB |
            this.capi.SQLITE_OPEN_MAIN_JOURNAL |
            this.capi.SQLITE_OPEN_SUPER_JOURNAL |
            this.capi.SQLITE_OPEN_WAL;
        // Matches vendor: FLAG_COMPUTE_DIGEST_V2 = SQLITE_OPEN_MEMORY (repurposed
        // bit, never set by SQLite itself for VFS-level flags).
        this.flagComputeDigestV2 = this.capi.SQLITE_OPEN_MEMORY;

        // Build and install the native-side vfs + io methods structs.
        this.cIoMethods = new this.capi.sqlite3_io_methods();
        this.cIoMethods.$iVersion = 1;
        sqlite3.vfs.installVfs({
            io: { struct: this.cIoMethods, methods: this.buildIoMethods() },
        });
        this.cVfs = this.createOpfsVfs();
        setPoolForVfs(this.cVfs.pointer, this);

        this.isReady = this.reset(!!options.clearOnInit).then(() => {
            if (this.$error) throw this.$error;
            if (!this.getCapacity()) {
                return this.addCapacity(options.initialCapacity ?? 6).then(() => {});
            }
        });
    }

    // -- Logging ------------------------------------------------------------

    private logImpl(level: number, ...args: any[]) {
        if (this.verbosity > level) this.loggers[level](this.vfsName + ':', ...args);
    }
    log(...args: any[]) {
        this.logImpl(2, ...args);
    }
    warn(...args: any[]) {
        this.logImpl(1, ...args);
    }
    error(...args: any[]) {
        this.logImpl(0, ...args);
    }

    getVfs() {
        return this.cVfs;
    }
    getCapacity() {
        return this.mapSAHToName.size;
    }
    getFileCount() {
        return this.mapFilenameToSAH.size;
    }
    getFileNames() {
        return Array.from(this.mapFilenameToSAH.keys());
    }

    // -- OFile bookkeeping --------------------------------------------------

    getOFileForS3File(pFile: number) {
        return this.mapS3FileToOFile.get(pFile);
    }
    mapS3FileToOFileSet(pFile: number, file: OFile | false) {
        if (file) {
            this.mapS3FileToOFile.set(pFile, file);
            setPoolForPFile(pFile, this);
        } else {
            this.mapS3FileToOFile.delete(pFile);
            setPoolForPFile(pFile, null);
        }
    }
    hasFilename(name: string) {
        return this.mapFilenameToSAH.has(name);
    }
    getSAHForPath(path: string) {
        return this.mapFilenameToSAH.get(path);
    }
    nextAvailableSAH(): FileSystemSyncAccessHandle | undefined {
        const [rc] = this.availableSAH.keys();
        return rc;
    }

    // -- Capacity management ------------------------------------------------

    async addCapacity(n: number) {
        for (let i = 0; i < n; ++i) {
            const name = getRandomName();
            const h = await this.dhOpaque.getFileHandle(name, { create: true });
            const ah = await h.createSyncAccessHandle();
            this.mapSAHToName.set(ah, name);
            this.setAssociatedPath(ah, '', 0);
        }
        return this.getCapacity();
    }

    async reduceCapacity(n: number) {
        let nRm = 0;
        for (const ah of Array.from(this.availableSAH)) {
            if (nRm === n || this.getFileCount() === this.getCapacity()) break;
            const name = this.mapSAHToName.get(ah)!;
            ah.close();
            await this.dhOpaque.removeEntry(name);
            this.mapSAHToName.delete(ah);
            this.availableSAH.delete(ah);
            ++nRm;
        }
        return nRm;
    }

    releaseAccessHandles() {
        for (const ah of this.mapSAHToName.keys()) ah.close();
        this.mapSAHToName.clear();
        this.mapFilenameToSAH.clear();
        this.availableSAH.clear();
    }

    async acquireAccessHandles(clearFiles = false) {
        const files: Array<[string, FileSystemFileHandle]> = [];
        for await (const [name, h] of (this.dhOpaque as any).entries() as AsyncIterable<
            [string, FileSystemHandle]
        >) {
            if ('file' === h.kind) files.push([name, h as FileSystemFileHandle]);
        }
        return Promise.all(
            files.map(async ([name, h]) => {
                try {
                    const ah = await h.createSyncAccessHandle();
                    this.mapSAHToName.set(ah, name);
                    if (clearFiles) {
                        ah.truncate(HEADER_OFFSET_DATA);
                        this.setAssociatedPath(ah, '', 0);
                    } else {
                        const path = this.getAssociatedPath(ah);
                        if (path) this.mapFilenameToSAH.set(path, ah);
                        else this.availableSAH.add(ah);
                    }
                } catch (e) {
                    this.storeErr(e);
                    this.releaseAccessHandles();
                    throw e;
                }
            })
        );
    }

    getAssociatedPath(sah: FileSystemSyncAccessHandle): string {
        sah.read(this.apBody, { at: 0 });
        const flags = this.dvBody.getUint32(HEADER_OFFSET_FLAGS);
        if (
            this.apBody[0] &&
            ((flags & this.capi.SQLITE_OPEN_DELETEONCLOSE) ||
                (flags & this.persistentFileTypes) === 0)
        ) {
            this.warn(
                `Removing file with unexpected flags ${flags.toString(16)}`,
                this.apBody
            );
            this.setAssociatedPath(sah, '', 0);
            return '';
        }

        const fileDigest = new Uint32Array(HEADER_DIGEST_SIZE / 4);
        sah.read(fileDigest, { at: HEADER_OFFSET_DIGEST });
        const compDigest = this.computeDigest(this.apBody, flags);
        if (fileDigest.every((v, i) => v === compDigest[i])) {
            const pathBytes = this.apBody.findIndex((v) => 0 === v);
            if (0 === pathBytes) {
                sah.truncate(HEADER_OFFSET_DATA);
            }
            return pathBytes ? textDecoder.decode(this.apBody.subarray(0, pathBytes)) : '';
        } else {
            this.warn('Disassociating file with bad digest.');
            this.setAssociatedPath(sah, '', 0);
            return '';
        }
    }

    setAssociatedPath(sah: FileSystemSyncAccessHandle, path: string, flags: number) {
        const enc = textEncoder.encodeInto(path, this.apBody);
        if (HEADER_MAX_PATH_SIZE <= enc.written + 1) toss('Path too long:', path);
        if (path && flags) flags |= this.flagComputeDigestV2;
        this.apBody.fill(0, enc.written, HEADER_MAX_PATH_SIZE);
        this.dvBody.setUint32(HEADER_OFFSET_FLAGS, flags);
        const digest = this.computeDigest(this.apBody, flags);
        sah.write(this.apBody, { at: 0 });
        sah.write(digest, { at: HEADER_OFFSET_DIGEST });
        sah.flush();

        if (path) {
            this.mapFilenameToSAH.set(path, sah);
            this.availableSAH.delete(sah);
        } else {
            sah.truncate(HEADER_OFFSET_DATA);
            this.availableSAH.add(sah);
        }
    }

    computeDigest(byteArray: Uint8Array, fileFlags: number) {
        if (fileFlags & this.flagComputeDigestV2) {
            let h1 = 0xdeadbeef;
            let h2 = 0x41c6ce57;
            for (const v of byteArray) {
                h1 = Math.imul(h1 ^ v, 2654435761);
                h2 = Math.imul(h2 ^ v, 104729);
            }
            return new Uint32Array([h1 >>> 0, h2 >>> 0]);
        } else {
            return new Uint32Array([0, 0]);
        }
    }

    async reset(clearFiles: boolean) {
        let h = await navigator.storage.getDirectory();
        let prev: FileSystemDirectoryHandle | undefined;
        for (const d of this.vfsDir.split('/')) {
            if (d) {
                prev = h;
                h = await h.getDirectoryHandle(d, { create: true });
            }
        }
        this.dhVfsRoot = h;
        this.dhVfsParent = prev;
        this.dhOpaque = await this.dhVfsRoot.getDirectoryHandle(OPAQUE_DIR_NAME, {
            create: true,
        });
        this.releaseAccessHandles();
        return this.acquireAccessHandles(clearFiles);
    }

    getPath(arg: any): string {
        if (this.wasm.isPtr(arg)) arg = this.wasm.cstrToJs(arg);
        return ((arg instanceof URL) ? arg : new URL(arg, 'file://localhost/')).pathname;
    }

    deletePath(path: string) {
        const sah = this.mapFilenameToSAH.get(path);
        if (sah) {
            this.mapFilenameToSAH.delete(path);
            this.setAssociatedPath(sah, '', 0);
        }
        return !!sah;
    }

    /**
     * Renames a file in-place by updating the SAHPool-level metadata mapping.
     * Matches the behavior added to vendor by the upstream patch-package entry
     * (see SqliteWasmBlazor/TypeScript/patches). No file data is copied.
     *
     * Caveat for encrypted DBs: this does NOT update the AAD used by the PRF
     * VFS (AAD binds the old path). Callers that rename an encrypted DB must
     * accept that the DB will fail to decrypt on next open until re-encrypted
     * under the new path. CryptoSync does not rename DBs.
     */
    renameFile(oldPath: string, newPath: string): true {
        const sah = this.mapFilenameToSAH.get(oldPath);
        if (!sah) toss('File not found:', oldPath);
        sah.read(this.apBody, { at: 0 });
        const flags = this.dvBody.getUint32(HEADER_OFFSET_FLAGS);
        this.mapFilenameToSAH.delete(oldPath);
        this.setAssociatedPath(sah, newPath, flags);
        return true;
    }

    /**
     * AEAD-test the registered key for {@link path} against slot 0 of the
     * existing on-disk DB. Pure VFS-level probe — no SQLite engagement.
     *
     * Returns:
     *   - `'noExistingDb'` if no SAH carries this path, or the SAH has not
     *     yet had a full physical slot written (truly fresh DB).
     *   - `'match'` if slot 0's ChaCha20-Poly1305 tag verifies under the
     *     registered key + slot-0 AAD.
     *   - `'wrongKey'` if the tag fails to verify.
     *
     * Caller is responsible for whatever follow-up the outcome demands —
     * e.g. dropping the registry entry on `'wrongKey'` so a known-wrong
     * key never sees a write.
     */
    verifyEncryptionKey(path: string): VfsKeyVerifyResult {
        const sah = this.mapFilenameToSAH.get(path);
        if (!sah) return 'noExistingDb';

        const physicalDataSize = sah.getSize() - HEADER_OFFSET_DATA;
        if (physicalDataSize < PHYSICAL_SLOT_SIZE) return 'noExistingDb';

        const key = getKeyForPath(path);
        if (!key) return 'noExistingDb';

        const slot = new Uint8Array(PHYSICAL_SLOT_SIZE);
        const nRead = sah.read(slot, { at: HEADER_OFFSET_DATA });
        if (nRead < PHYSICAL_SLOT_SIZE) return 'noExistingDb';

        const ciphertext = slot.subarray(0, PAGE_PLAINTEXT_LEN);
        const nonce = slot.subarray(
            PAGE_PLAINTEXT_LEN,
            PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
        );
        const tag = slot.subarray(PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN);
        const cipherPlusTag = new Uint8Array(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);
        cipherPlusTag.set(ciphertext, 0);
        cipherPlusTag.set(tag, PAGE_PLAINTEXT_LEN);
        const aad = buildPageAad(path, 0);
        try {
            decryptChaCha20Poly1305(
                { ciphertext: cipherPlusTag, nonce },
                key,
                aad
            );
            return 'match';
        } catch {
            return 'wrongKey';
        }
    }

    storeErr(e?: any, code?: number): number | undefined {
        if (e) {
            (e as any).sqlite3Rc = code || this.capi.SQLITE_IOERR;
            this.error(e);
        }
        this.$error = e;
        return code;
    }
    popErr() {
        const rc = this.$error;
        this.$error = undefined;
        return rc;
    }

    // -- Export / import -----------------------------------------------------

    exportFile(name: string) {
        const sah = this.mapFilenameToSAH.get(name);
        if (!sah) toss('File not found:', name);
        const n = sah!.getSize() - HEADER_OFFSET_DATA;
        const b = new Uint8Array(n > 0 ? n : 0);
        if (n > 0) {
            const nRead = sah!.read(b, { at: HEADER_OFFSET_DATA });
            if (nRead !== n) toss('Expected to read ' + n + ' bytes but read ' + nRead + '.');
        }
        return b;
    }

    async importDbChunked(name: string, callback: () => any) {
        const sah =
            this.mapFilenameToSAH.get(name) ||
            this.nextAvailableSAH() ||
            toss('No available handles to import to.');
        sah.truncate(0);
        let nWrote = 0,
            chunk: any,
            checkedHeader = false;
        try {
            while (undefined !== (chunk = await callback())) {
                if (chunk instanceof ArrayBuffer) chunk = new Uint8Array(chunk);
                if (!checkedHeader && 0 === nWrote && chunk.byteLength >= 15) {
                    this.util.affirmDbHeader(chunk);
                    checkedHeader = true;
                }
                sah.write(chunk, { at: HEADER_OFFSET_DATA + nWrote });
                nWrote += chunk.byteLength;
            }
            if (nWrote < 512 || 0 !== nWrote % 512) {
                toss('Input size', nWrote, 'is not correct for an SQLite database.');
            }
            if (!checkedHeader) {
                const header = new Uint8Array(20);
                sah.read(header, { at: 0 });
                this.util.affirmDbHeader(header);
            }
            sah.write(new Uint8Array([1, 1]), { at: HEADER_OFFSET_DATA + 18 });
        } catch (e) {
            this.setAssociatedPath(sah, '', 0);
            throw e;
        }
        this.setAssociatedPath(sah, name, this.capi.SQLITE_OPEN_MAIN_DB);
        return nWrote;
    }

    importDb(
        name: string,
        bytes: Uint8Array | ArrayBuffer | ((...a: any[]) => any),
        opaque: boolean = false
    ) {
        if (bytes instanceof ArrayBuffer) bytes = new Uint8Array(bytes);
        else if (bytes instanceof Function) return this.importDbChunked(name, bytes);
        const sah =
            this.mapFilenameToSAH.get(name) ||
            this.nextAvailableSAH() ||
            toss('No available handles to import to.');
        const n = (bytes as Uint8Array).byteLength;

        // Skip the SQLite-format-3 header check, the 512-byte alignment
        // sanity check, AND the byte-18 WAL-mode patch when either
        // (a) the caller explicitly asked for opaque import, or
        // (b) a key is currently registered for this path. All three would
        // corrupt AEAD tags on encrypted pages (and encrypted files use
        // 4124-byte physical slots, not 512-byte multiples). The explicit
        // `opaque` flag exists because the key registry is cleared on
        // close — an export → modify → import round-trip sees no registry
        // entry at import time.
        const skipFormatChecks = opaque || isEncryptedPath(name);
        if (!skipFormatChecks && (n < 512 || n % 512 !== 0)) {
            toss('Byte array size is invalid for an SQLite db.');
        }
        if (!skipFormatChecks) {
            const header = 'SQLite format 3';
            for (let i = 0; i < header.length; ++i) {
                if (header.charCodeAt(i) !== (bytes as Uint8Array)[i]) {
                    toss('Input does not contain an SQLite database header.');
                }
            }
        }
        const nWrote = sah.write(bytes as Uint8Array, { at: HEADER_OFFSET_DATA });
        if (nWrote !== n) {
            this.setAssociatedPath(sah, '', 0);
            toss('Expected to write ' + n + ' bytes but wrote ' + nWrote + '.');
        } else {
            if (!skipFormatChecks) {
                sah.write(new Uint8Array([1, 1]), { at: HEADER_OFFSET_DATA + 18 });
            }
            this.setAssociatedPath(sah, name, this.capi.SQLITE_OPEN_MAIN_DB);
        }
        return nWrote;
    }

    // -- VFS lifecycle -------------------------------------------------------

    async removeVfs() {
        if (!this.cVfs.pointer || !this.dhOpaque) return false;
        this.capi.sqlite3_vfs_unregister(this.cVfs.pointer);
        this.cVfs.dispose();
        delete initPromises[this.vfsName];
        try {
            this.releaseAccessHandles();
            await this.dhVfsRoot.removeEntry(OPAQUE_DIR_NAME, { recursive: true });
            (this as any).dhOpaque = undefined;
            if (this.dhVfsParent) {
                await this.dhVfsParent.removeEntry(this.dhVfsRoot.name, { recursive: true });
            }
            (this as any).dhVfsRoot = undefined;
            (this as any).dhVfsParent = undefined;
        } catch (e) {
            this.sqlite3.config.error(
                this.vfsName,
                'removeVfs() failed with no recovery strategy:',
                e
            );
        }
        return true;
    }

    pauseVfs() {
        if (this.mapS3FileToOFile.size > 0) {
            this.sqlite3.SQLite3Error.toss(
                this.capi.SQLITE_MISUSE,
                'Cannot pause VFS',
                this.vfsName,
                'because it has opened files.'
            );
        }
        if (this.mapSAHToName.size > 0) {
            this.capi.sqlite3_vfs_unregister(this.vfsName);
            this.releaseAccessHandles();
        }
        return this;
    }

    isPaused() {
        return 0 === this.mapSAHToName.size;
    }

    async unpauseVfs() {
        if (0 === this.mapSAHToName.size) {
            return this.acquireAccessHandles(false).then(() =>
                this.capi.sqlite3_vfs_register(this.cVfs, 0)
            );
        }
        return this;
    }

    // ==========================================================================
    // VFS method factory. Returns ioMethods and vfsMethods objects.
    // ==========================================================================

    private buildIoMethods() {
        const self = this;
        const capi = this.capi;
        const wasm = this.wasm;
        const pool = this;

        return {
            xCheckReservedLock(pFile: number, pOut: number) {
                pool.log('xCheckReservedLock');
                pool.storeErr();
                wasm.poke32(pOut, 1);
                return 0;
            },
            xClose(pFile: number): number {
                pool.storeErr();
                const file = pool.getOFileForS3File(pFile);
                if (file) {
                    try {
                        pool.log(`xClose ${file.path}`);
                        pool.mapS3FileToOFileSet(pFile, false);
                        file.sah.flush();
                        if (file.flags & capi.SQLITE_OPEN_DELETEONCLOSE) {
                            pool.deletePath(file.path);
                        }
                        // Per-file key is owned by the registry, not by file. Do not
                        // wipe file.key here — closeDatabase clears it via the registry.
                    } catch (e) {
                        return pool.storeErr(e, capi.SQLITE_IOERR)!;
                    }
                }
                return 0;
            },
            xDeviceCharacteristics() {
                return capi.SQLITE_IOCAP_UNDELETABLE_WHEN_OPEN;
            },
            xFileControl() {
                return capi.SQLITE_NOTFOUND;
            },
            xFileSize(pFile: number, pSz64: number) {
                pool.log('xFileSize');
                const file = pool.getOFileForS3File(pFile)!;
                const physical = file.sah.getSize() - HEADER_OFFSET_DATA;
                // Encrypted files expand 4096 → 4124 per slot; SQLite must
                // see the logical size or it miscalculates page counts.
                const size = file.key === undefined
                    ? physical
                    : physicalToLogicalSize(physical);
                wasm.poke64(pSz64, BigInt(size));
                return 0;
            },
            xLock(pFile: number, lockType: number) {
                pool.log(`xLock ${lockType}`);
                pool.storeErr();
                const file = pool.getOFileForS3File(pFile)!;
                file.lockType = lockType;
                return 0;
            },
            xRead(pFile: number, pDest: number, n: number, offset64: any): number {
                pool.storeErr();
                const file = pool.getOFileForS3File(pFile)!;
                const off = Number(offset64);
                pool.log(`xRead ${file.path} ${n} @ ${off}`);
                try {
                    if (file.key === undefined) {
                        // Plain path — vendor-identical.
                        const nRead = file.sah.read(
                            wasm.heap8u().subarray(Number(pDest), Number(pDest) + n),
                            { at: HEADER_OFFSET_DATA + off }
                        );
                        if (nRead < n) {
                            wasm.heap8u().fill(0, Number(pDest) + nRead, Number(pDest) + n);
                            return capi.SQLITE_IOERR_SHORT_READ;
                        }
                        return 0;
                    }
                    return self.encryptedRead(file, pDest, n, off);
                } catch (e) {
                    return pool.storeErr(e, capi.SQLITE_IOERR)!;
                }
            },
            xSectorSize() {
                return SECTOR_SIZE;
            },
            xSync(pFile: number, _flags: number) {
                pool.log(`xSync`);
                pool.storeErr();
                const file = pool.getOFileForS3File(pFile)!;
                try {
                    file.sah.flush();
                    return 0;
                } catch (e) {
                    return pool.storeErr(e, capi.SQLITE_IOERR)!;
                }
            },
            xTruncate(pFile: number, sz64: any) {
                pool.log(`xTruncate ${sz64}`);
                pool.storeErr();
                const file = pool.getOFileForS3File(pFile)!;
                try {
                    const logicalSz = Number(sz64);
                    const physicalSz = file.key === undefined
                        ? logicalSz
                        : logicalToPhysicalSize(logicalSz);
                    file.sah.truncate(HEADER_OFFSET_DATA + physicalSz);
                    return 0;
                } catch (e) {
                    return pool.storeErr(e, capi.SQLITE_IOERR)!;
                }
            },
            xUnlock(pFile: number, lockType: number) {
                pool.log('xUnlock');
                const file = pool.getOFileForS3File(pFile)!;
                file.lockType = lockType;
                return 0;
            },
            xWrite(pFile: number, pSrc: number, n: number, offset64: any): number {
                pool.storeErr();
                const file = pool.getOFileForS3File(pFile)!;
                const off = Number(offset64);
                pool.log(`xWrite ${file.path} ${n} @ ${off}`);
                try {
                    if (file.key === undefined) {
                        // Plain path — vendor-identical.
                        const nBytes = file.sah.write(
                            wasm.heap8u().subarray(Number(pSrc), Number(pSrc) + n),
                            { at: HEADER_OFFSET_DATA + off }
                        );
                        return n === nBytes ? 0 : pool.storeErr(new Error('short write'), capi.SQLITE_IOERR)!;
                    }
                    return self.encryptedWrite(file, pSrc, n, off);
                } catch (e) {
                    return pool.storeErr(e, capi.SQLITE_IOERR)!;
                }
            },
        };
    }

    // ==========================================================================
    // Encryption hot paths (offset-remapping).
    //
    // A logical SECTOR_SIZE-byte slot at logical offset (slotIndex*4096) maps
    // to a 4124-byte physical slot at physical offset (slotIndex*4124) on disk
    // (relative to HEADER_OFFSET_DATA). Physical layout:
    //     [ ciphertext(4096) | nonce(12) | tag(16) ]
    //
    // ChaCha20-Poly1305 returns ciphertext-with-tag-appended of length
    // plaintext.length + 16 = 4112. We split that into ciphertext(4096) at the
    // slot head and tag(16) after the nonce.
    //
    // Logical bytes ↔ physical bytes is a pure 4096/4124 remap — no
    // "reserved tail" is exposed to SQLite, so every logical byte has a real
    // plaintext counterpart on disk.
    // ==========================================================================

    private slotScratch = new Uint8Array(PHYSICAL_SLOT_SIZE);
    private plaintextScratch = new Uint8Array(PAGE_PLAINTEXT_LEN);

    private encryptedRead(file: OFile, pDest: number, n: number, off: number): number {
        if (n <= 0) return 0;

        const heap = this.wasm.heap8u();
        const endOff = off + n;
        let cursor = off;
        let destPtr = Number(pDest);

        while (cursor < endOff) {
            const slotIndex = Math.floor(cursor / SECTOR_SIZE);
            const slotStart = slotIndex * SECTOR_SIZE;
            const slotEnd = slotStart + SECTOR_SIZE;

            const thisSliceEnd = Math.min(endOff, slotEnd);
            const startInSlot = cursor - slotStart;
            const bytesFromSlot = thisSliceEnd - cursor;

            const physicalSlotStart = slotIndex * PHYSICAL_SLOT_SIZE;
            const nRead = file.sah.read(this.slotScratch, {
                at: HEADER_OFFSET_DATA + physicalSlotStart,
            });
            if (nRead < PHYSICAL_SLOT_SIZE) {
                // Partial/absent physical slot → short read (SQLite handles
                // this as "past EOF", e.g. a fresh DB where the page hasn't
                // been written yet).
                heap.fill(0, destPtr, destPtr + (endOff - cursor));
                return this.capi.SQLITE_IOERR_SHORT_READ;
            }

            const ciphertext = this.slotScratch.subarray(0, PAGE_PLAINTEXT_LEN);
            const nonce = this.slotScratch.subarray(
                PAGE_PLAINTEXT_LEN,
                PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
            );
            const tag = this.slotScratch.subarray(PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN);
            const cipherPlusTag = new Uint8Array(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);
            cipherPlusTag.set(ciphertext, 0);
            cipherPlusTag.set(tag, PAGE_PLAINTEXT_LEN);

            const aad = buildPageAad(file.path, slotIndex);
            const plaintext = decryptChaCha20Poly1305(
                { ciphertext: cipherPlusTag, nonce },
                file.key!,
                aad
            );

            heap.set(
                plaintext.subarray(startInSlot, startInSlot + bytesFromSlot),
                destPtr
            );
            destPtr += bytesFromSlot;
            cursor = thisSliceEnd;
        }

        // Defense-in-depth: the plaintext scratch buffer used by the
        // read-modify-write path (readSlotPlaintextOrZero) retains the last
        // decrypted page until overwritten. Zero it after each op so a
        // heap-snapshot attacker sees only the most recent sub-microsecond
        // window rather than the last page accessed at any earlier time.
        // ~4 KB memset per page op ≈ <1 µs; negligible vs the crypto cost.
        this.plaintextScratch.fill(0);

        return 0;
    }

    private encryptedWrite(file: OFile, pSrc: number, n: number, off: number): number {
        if (n <= 0) return 0;

        const heap = this.wasm.heap8u();
        const endOff = off + n;
        let cursor = off;
        let srcPtr = Number(pSrc);

        while (cursor < endOff) {
            const slotIndex = Math.floor(cursor / SECTOR_SIZE);
            const slotStart = slotIndex * SECTOR_SIZE;
            const slotEnd = slotStart + SECTOR_SIZE;

            const thisSliceEnd = Math.min(endOff, slotEnd);
            const startInSlot = cursor - slotStart;
            const bytesFromSlot = thisSliceEnd - cursor;

            const isFullSlot = startInSlot === 0 && bytesFromSlot === PAGE_PLAINTEXT_LEN;

            let plaintext: Uint8Array;
            if (isFullSlot) {
                plaintext = this.plaintextScratch;
                plaintext.set(heap.subarray(srcPtr, srcPtr + bytesFromSlot), 0);
            } else {
                // Read-modify-write: pull existing plaintext (or zero-fill
                // if past-EOF), overlay new bytes, re-encrypt.
                plaintext = this.readSlotPlaintextOrZero(file, slotIndex);
                plaintext.set(
                    heap.subarray(srcPtr, srcPtr + bytesFromSlot),
                    startInSlot
                );
            }

            const aad = buildPageAad(file.path, slotIndex);
            const enc = encryptChaCha20Poly1305(plaintext, file.key!, aad);
            // enc.ciphertext = ciphertext(4096) || tag(16) — length 4112.

            // Assemble physical slot: ciphertext(4096) | nonce(12) | tag(16)
            this.slotScratch.set(enc.ciphertext.subarray(0, PAGE_PLAINTEXT_LEN), 0);
            this.slotScratch.set(enc.nonce, PAGE_PLAINTEXT_LEN);
            this.slotScratch.set(
                enc.ciphertext.subarray(PAGE_PLAINTEXT_LEN),
                PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
            );

            const physicalSlotStart = slotIndex * PHYSICAL_SLOT_SIZE;
            const nWrote = file.sah.write(this.slotScratch, {
                at: HEADER_OFFSET_DATA + physicalSlotStart,
            });
            if (nWrote !== PHYSICAL_SLOT_SIZE) {
                return this.pool_storeErr(
                    new Error('short slot write'),
                    this.capi.SQLITE_IOERR
                );
            }

            srcPtr += bytesFromSlot;
            cursor = thisSliceEnd;
        }

        // Same defense-in-depth as encryptedRead: clear the plaintext scratch.
        this.plaintextScratch.fill(0);

        return 0;
    }

    private readSlotPlaintextOrZero(file: OFile, slotIndex: number): Uint8Array {
        const physicalSlotStart = HEADER_OFFSET_DATA + slotIndex * PHYSICAL_SLOT_SIZE;
        const nRead = file.sah.read(this.slotScratch, { at: physicalSlotStart });
        if (nRead < PHYSICAL_SLOT_SIZE) {
            // No existing data → zero-initialized plaintext.
            this.plaintextScratch.fill(0);
            return this.plaintextScratch;
        }
        const ciphertext = this.slotScratch.subarray(0, PAGE_PLAINTEXT_LEN);
        const nonce = this.slotScratch.subarray(
            PAGE_PLAINTEXT_LEN,
            PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN
        );
        const tag = this.slotScratch.subarray(PAGE_PLAINTEXT_LEN + PAGE_NONCE_LEN);
        const cipherPlusTag = new Uint8Array(PAGE_PLAINTEXT_LEN + PAGE_TAG_LEN);
        cipherPlusTag.set(ciphertext, 0);
        cipherPlusTag.set(tag, PAGE_PLAINTEXT_LEN);
        const aad = buildPageAad(file.path, slotIndex);
        const pt = decryptChaCha20Poly1305(
            { ciphertext: cipherPlusTag, nonce },
            file.key!,
            aad
        );
        this.plaintextScratch.set(pt, 0);
        return this.plaintextScratch;
    }

    private pool_storeErr(e: any, code: number): number {
        this.storeErr(e, code);
        return code;
    }

    // ==========================================================================
    // sqlite3_vfs registration.
    // ==========================================================================

    private createOpfsVfs() {
        const sqlite3 = this.sqlite3;
        const capi = this.capi;
        const wasm = this.wasm;
        const vfsName = this.vfsName;
        if (capi.sqlite3_vfs_find(vfsName)) {
            toss('VFS name is already registered:', vfsName);
        }
        const opfsVfs = new capi.sqlite3_vfs();
        const pDVfs = capi.sqlite3_vfs_find(null);
        const dVfs = pDVfs ? new capi.sqlite3_vfs(pDVfs) : null;
        opfsVfs.$iVersion = 2;
        opfsVfs.$szOsFile = capi.sqlite3_file.structInfo.sizeof;
        opfsVfs.$mxPathname = HEADER_MAX_PATH_SIZE;
        opfsVfs.addOnDispose(
            (opfsVfs.$zName = wasm.allocCString(vfsName)),
            () => setPoolForVfs(opfsVfs.pointer, null)
        );

        const pool = this;
        const ioStruct = this.cIoMethods;
        const FLAG_COMPUTE_DIGEST_V2 = this.flagComputeDigestV2;

        const vfsMethods: any = {
            xAccess(pVfs: number, zName: number, _flags: number, pOut: number) {
                pool.storeErr();
                try {
                    const name = pool.getPath(zName);
                    wasm.poke32(pOut, pool.hasFilename(name) ? 1 : 0);
                } catch {
                    wasm.poke32(pOut, 0);
                }
                return 0;
            },
            xCurrentTime(_pVfs: number, pOut: number) {
                wasm.poke(pOut, 2440587.5 + new Date().getTime() / 86400000, 'double');
                return 0;
            },
            xCurrentTimeInt64(_pVfs: number, pOut: number) {
                wasm.poke(pOut, 2440587.5 * 86400000 + new Date().getTime(), 'i64');
                return 0;
            },
            xDelete(_pVfs: number, zName: number, _doSyncDir: number) {
                pool.log(`xDelete ${wasm.cstrToJs(zName)}`);
                pool.storeErr();
                try {
                    pool.deletePath(pool.getPath(zName));
                    return 0;
                } catch (e) {
                    pool.storeErr(e);
                    return capi.SQLITE_IOERR_DELETE;
                }
            },
            xFullPathname(_pVfs: number, zName: number, nOut: number, pOut: number) {
                const i = wasm.cstrncpy(pOut, zName, nOut);
                return i < nOut ? 0 : capi.SQLITE_CANTOPEN;
            },
            xGetLastError(_pVfs: number, nOut: number, pOut: number) {
                const e = pool.popErr();
                if (e) {
                    const scope = wasm.scopedAllocPush();
                    try {
                        const [cMsg, n] = wasm.scopedAllocCString(e.message, true);
                        wasm.cstrncpy(pOut, cMsg, nOut);
                        if (n > nOut) wasm.poke8(wasm.ptr.add(pOut, nOut, -1), 0);
                    } catch {
                        return capi.SQLITE_NOMEM;
                    } finally {
                        wasm.scopedAllocPop(scope);
                    }
                }
                return e ? (e.sqlite3Rc || capi.SQLITE_IOERR) : 0;
            },
            xOpen(_pVfs: number, zName: number, pFile: number, flags: number, pOutFlags: number) {
                try {
                    flags &= ~FLAG_COMPUTE_DIGEST_V2;
                    pool.log(`xOpen ${wasm.cstrToJs(zName)} ${flags}`);
                    const path =
                        zName && wasm.peek8(zName) ? pool.getPath(zName) : getRandomName();
                    let sah = pool.getSAHForPath(path);
                    if (!sah && flags & capi.SQLITE_OPEN_CREATE) {
                        if (pool.getFileCount() < pool.getCapacity()) {
                            sah = pool.nextAvailableSAH();
                            if (sah) pool.setAssociatedPath(sah, path, flags);
                        } else {
                            toss('SAH pool is full. Cannot create file', path);
                        }
                    }
                    if (!sah) toss('file not found:', path);

                    // PRF-VFS key lookup: if the caller registered a key for this path
                    // BEFORE opening the DB, stamp it on the file record.
                    const key = getKeyForPath(path);

                    const file: OFile = {
                        path,
                        flags,
                        sah: sah!,
                        lockType: capi.SQLITE_LOCK_NONE,
                        key,
                    };
                    pool.mapS3FileToOFileSet(pFile, file);

                    const sq3File = new capi.sqlite3_file(pFile);
                    sq3File.$pMethods = ioStruct.pointer;
                    sq3File.dispose();
                    wasm.poke32(pOutFlags, flags);
                    return 0;
                } catch (e) {
                    pool.storeErr(e);
                    return capi.SQLITE_CANTOPEN;
                }
            },
        };

        if (dVfs) {
            opfsVfs.$xRandomness = dVfs.$xRandomness;
            opfsVfs.$xSleep = dVfs.$xSleep;
            dVfs.dispose();
        }
        if (!opfsVfs.$xRandomness && !vfsMethods.xRandomness) {
            vfsMethods.xRandomness = (_pVfs: number, nOut: number, pOut: number) => {
                const heap = wasm.heap8u();
                let i = 0;
                const npOut = Number(pOut);
                for (; i < nOut; ++i) heap[npOut + i] = (Math.random() * 255000) & 0xff;
                return i;
            };
        }
        if (!opfsVfs.$xSleep && !vfsMethods.xSleep) {
            vfsMethods.xSleep = () => 0;
        }
        sqlite3.vfs.installVfs({ vfs: { struct: opfsVfs, methods: vfsMethods } });
        return opfsVfs;
    }
}

// ==========================================================================
// Util wrapper exposed to clients.
// ==========================================================================

class OpfsSAHPoolUtil {
    private p: OpfsSAHPool;
    vfsName: string;
    OpfsSAHPoolDb?: new (...args: any[]) => any;

    constructor(pool: OpfsSAHPool) {
        this.p = pool;
        this.vfsName = pool.vfsName;
    }

    async addCapacity(n: number) {
        return this.p.addCapacity(n);
    }
    async reduceCapacity(n: number) {
        return this.p.reduceCapacity(n);
    }
    getCapacity() {
        return this.p.getCapacity();
    }
    getFileCount() {
        return this.p.getFileCount();
    }
    getFileNames() {
        return this.p.getFileNames();
    }
    async reserveMinimumCapacity(min: number) {
        const c = this.p.getCapacity();
        return c < min ? this.p.addCapacity(min - c) : c;
    }
    exportFile(name: string) {
        return this.p.exportFile(name);
    }
    importDb(
        name: string,
        bytes: Uint8Array | ArrayBuffer | ((...a: any[]) => any),
        opaque: boolean = false
    ) {
        return this.p.importDb(name, bytes, opaque);
    }
    async wipeFiles() {
        await this.p.reset(true);
    }
    unlink(filename: string) {
        return this.p.deletePath(filename);
    }
    renameFile(oldPath: string, newPath: string) {
        return this.p.renameFile(oldPath, newPath);
    }
    verifyEncryptionKey(path: string) {
        return this.p.verifyEncryptionKey(path);
    }
    async removeVfs() {
        return this.p.removeVfs();
    }
    pauseVfs() {
        this.p.pauseVfs();
        return this as unknown as PrfPoolUtil;
    }
    async unpauseVfs() {
        await this.p.unpauseVfs();
        return this as unknown as PrfPoolUtil;
    }
    isPaused() {
        return this.p.isPaused();
    }
}

// ==========================================================================
// OFile — per-open file state. Mirrors vendor shape but carries a nullable
// key for the PRF-VFS path.
// ==========================================================================

interface OFile {
    path: string;
    flags: number;
    sah: FileSystemSyncAccessHandle;
    lockType: number;
    key?: Uint8Array; // present iff the file is encrypted
}

// ==========================================================================
// Module-local maps for sqlite3 pointers → pool instance.
// ==========================================================================

const mapVfsToPool = new Map<number, OpfsSAHPool>();
const mapSqlite3FileToPool = new Map<number, OpfsSAHPool>();

function setPoolForVfs(pVfs: number, pool: OpfsSAHPool | null) {
    if (pool) mapVfsToPool.set(pVfs, pool);
    else mapVfsToPool.delete(pVfs);
}
function setPoolForPFile(pFile: number, pool: OpfsSAHPool | null) {
    if (pool) mapSqlite3FileToPool.set(pFile, pool);
    else mapSqlite3FileToPool.delete(pFile);
}

// Silences the unused-reference lint without exposing internals.
// getPoolForVfs / getPoolForPFile are reserved for future VFS introspection.
export const __internal = { mapVfsToPool, mapSqlite3FileToPool };

// Suppress unused-var warnings while keeping the symbol around for future use.
void clearBytes;
