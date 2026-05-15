// sqlite-worker.ts
// Web Worker for executing SQL with sqlite-wasm + OPFS SAHPool VFS
// SAHPool provides synchronous OPFS access in worker context

import sqlite3InitModule from '@sqlite.org/sqlite-wasm';
import { pack, unpack } from 'msgpackr';
import {
    logger,
    registerEFCoreFunctions,
    openDatabases, pragmasSet, schemaCache,
    MODULE_NAME, bigIntUnpackr,
    setSqlite3, setPoolUtil, setBaseHref,
    bulkInsertRows, type BulkInsertHeader,
} from '@sqlitewasmblazor/worker-common';
import { deltaExportEncrypted, deltaImportEncrypted, bulkRotateKey } from './crypto-delta';
import { installOpfsSAHPoolVfs as installPrfVfs } from './vfs-prf/sahpool-prf-vfs';
import {
    hasGlobalKey,
    snapshotGlobalKey,
    setGlobalKey,
    clearGlobalKey,
} from './vfs-prf/key-registry';
import { rekeySlots } from './vfs-prf/rekey';
import { clearBytes } from '@sqlitewasmblazor/crypto-core';
import {
    readDiskManifestOp,
    writeDiskManifestOp,
    clearDiskManifestOp,
} from './worker-manifest';

// Re-export mutable state references for local use
let sqlite3: any;
let poolUtil: any;
let baseHref = '/';
// Asset resolution path, received in the 'init' message from the bridge.
// Override (e.g. "content/SqliteWasmBlazor/") supports browser-extension builds
// that flatten the underscore-prefixed _content path.
let assetRoot = '_content/SqliteWasmBlazor/';

interface WorkerRequest {
    id: number;
    data: {
        type: string;
        database?: string;
        sql?: string;
        parameters?: Record<string, any>;
    };
    binaryPayload?: ArrayBuffer;
    binaryHeader?: ArrayBuffer;
}

interface WorkerResponse {
    id: number;
    data: {
        success: boolean;
        error?: string;
        columnNames?: string[];
        columnTypes?: string[];
        typedRows?: {
            types: string[];
            data: any[][];
        };
        rowsAffected?: number;
        lastInsertId?: number;
    };
}

// Helper function to convert BigInt and Uint8Array for JSON serialization
// BigInts within safe integer range (±2^53-1) are converted to number for efficiency
// Larger BigInts are converted to string to preserve precision
// Uint8Arrays are converted to Base64 strings (matches .NET 6+ JSInterop convention)
// Convert BigInt values for MessagePack serialization
// MessagePack natively handles Uint8Array, so no Base64 conversion needed
function convertBigInt(value: any): any {
    if (typeof value === 'bigint') {
        // Check if BigInt fits in JavaScript's safe integer range
        if (value >= Number.MIN_SAFE_INTEGER && value <= Number.MAX_SAFE_INTEGER) {
            return Number(value);  // Convert to number for efficiency
        }
        return value.toString();  // Convert to string to preserve precision
    }
    if (Array.isArray(value)) {
        return value.map(convertBigInt);
    }
    if (value && typeof value === 'object' && !(value instanceof Uint8Array)) {
        const converted: any = {};
        for (const key in value) {
            converted[key] = convertBigInt(value[key]);
        }
        return converted;
    }
    return value;
}

// Initialize sqlite-wasm with OPFS SAHPool
async function initializeSQLite() {
    try {
        logger.info(MODULE_NAME, 'Initializing sqlite-wasm with OPFS SAHPool...');

        // Temporarily intercept console.warn to suppress sqlite3.wasm OPFS warnings during initialization
        const originalWarn = console.warn;
        console.warn = (...args: any[]) => {
            const message = args.join(' ');
            if (message.includes('Ignoring inability to install OPFS') ||
                message.includes('sqlite3_vfs') ||
                message.includes('Cannot install OPFS') ||
                message.includes('Missing SharedArrayBuffer') ||
                message.includes('COOP/COEP')) {
                // Suppress warning about standard OPFS - we use SAHPool instead
                return;
            }
            originalWarn.apply(console, args);
        };

        // Type declarations don't expose Emscripten-style init options,
        // but the runtime accepts them for locateFile, print, and printErr
        const initOptions = {
            print: console.log,
            printErr: console.error,
            locateFile(path: string) {
                if (path.endsWith('.wasm')) {
                    return `${baseHref}${assetRoot}${path}`;
                }
                return path;
            }
        };
        sqlite3 = await (sqlite3InitModule as (options: typeof initOptions) => Promise<typeof sqlite3>)(initOptions);
        setSqlite3(sqlite3);

        // Restore original console.warn
        console.warn = originalWarn;

        // Configure SQLite's internal logging to respect our log level
        // This ensures SQLite WASM's warnings, errors, and debug messages go through our logger
        if (sqlite3.config) {
            sqlite3.config.warn = (...args: any[]) => logger.warn(MODULE_NAME, ...args);
            sqlite3.config.error = (...args: any[]) => logger.error(MODULE_NAME, ...args);
            sqlite3.config.log = (...args: any[]) => logger.info(MODULE_NAME, ...args);
            sqlite3.config.debug = (...args: any[]) => logger.debug(MODULE_NAME, ...args);
        }

        // Disable automatic OPFS VFS installation to prevent misleading warnings
        // We explicitly use SAHPool VFS below instead
        if ((sqlite3 as any).capi?.sqlite3_vfs_find('opfs')) {
            logger.debug(MODULE_NAME, 'OPFS VFS auto-installed, but we use SAHPool VFS instead');
        }

        // Install PRF-keyed OPFS SAHPool VFS.
        // This fork of sqlite-wasm's `opfs-sahpool` is a drop-in replacement:
        // - For DBs opened without a registered key, it behaves byte-for-byte
        //   like vendor (pass-through to the SAH).
        // - For DBs with a registered key, each page is encrypted via
        //   ChaCha20-Poly1305 (see vfs-prf/sahpool-prf-vfs.ts).
        // Registered under the same name ('opfs-sahpool') and same directory
        // ('/databases') so non-CryptoSync consumers see no change.
        //
        // Pool capacity: each DB occupies 1 slot for the main file; in
        // journal_mode=WAL it may also claim `.db-wal` and `.db-shm` slots
        // plus a transient `.db-journal` during the WAL mode transition
        // (~4 slots per active WAL DB). Encrypted DBs use journal_mode=MEMORY
        // and only need the 1 main slot. For apps that open multiple DBs
        // (TodoDb + CryptoTestDb + EncryptedTestDb + PasswordTestDb +
        // per-feature benchmarks) 10 slots is tight — we default to 25 so
        // a realistic workload doesn't trip "SAH pool is full" on journal
        // creation. 25 × ~4 KiB preallocated = ~100 KiB, negligible.
        poolUtil = await installPrfVfs(sqlite3, {
            initialCapacity: 25,
            directory: '/databases',
            name: 'opfs-sahpool',
            clearOnInit: false
        });
        setPoolUtil(poolUtil);

        // Grow pool if previously created with smaller capacity (initialCapacity only applies on first creation)
        await poolUtil.reserveMinimumCapacity(25);

        logger.info(MODULE_NAME, 'OPFS SAHPool VFS installed successfully');
        logger.debug(MODULE_NAME, 'Available VFS:', sqlite3.capi.sqlite3_vfs_find(null));

        // Signal ready to main thread
        self.postMessage({ type: 'ready' });
        logger.info(MODULE_NAME, 'Ready!');
    } catch (error) {
        logger.error(MODULE_NAME, 'Initialization failed:', error);
        self.postMessage({
            type: 'error',
            error: error instanceof Error ? error.message : 'Unknown initialization error'
        });
    }
}

// Handle messages from main thread
self.onmessage = async (event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>) => {
    // Handle initialization with base href and asset root
    if ('type' in event.data && event.data.type === 'init' && 'baseHref' in event.data) {
        baseHref = event.data.baseHref;
        setBaseHref(baseHref);
        if (event.data.assetRoot) {
            assetRoot = event.data.assetRoot;
        }
        // Start initialization after receiving base href
        await initializeSQLite();
        return;
    }

    // Handle log level changes (no response needed)
    if ('type' in event.data && event.data.type === 'setLogLevel' && 'level' in event.data) {
        logger.setLogLevel(event.data.level);
        return;
    }

    // Handle regular requests
    const { id, data, binaryPayload, binaryHeader } = event.data as WorkerRequest;

    try {
        const result = await handleRequest(data, binaryPayload, binaryHeader);

        // Check if result contains raw binary data (export operations)
        if (result && typeof result === 'object' && 'rawBinary' in result && result.rawBinary) {
            const binaryData = result.data as Uint8Array;
            self.postMessage({
                id,
                rawBinary: true,
                data: binaryData
            }, [binaryData.buffer]);
        }
        // Check if result is MessagePack binary (Uint8Array)
        else if (result instanceof Uint8Array) {
            self.postMessage({
                id,
                binary: true,
                data: result
            });
        } else {
            // JSON response for non-execute operations
            const response: WorkerResponse = {
                id,
                data: {
                    success: true,
                    ...result
                }
            };
            self.postMessage(response);
        }
    } catch (error) {
        const response: WorkerResponse = {
            id,
            data: {
                success: false,
                error: error instanceof Error ? error.message : 'Unknown error'
            }
        };

        self.postMessage(response);
    }
};

async function handleRequest(data: WorkerRequest['data'], binaryPayload?: ArrayBuffer, binaryHeader?: ArrayBuffer) {
    const { type, database, sql, parameters } = data;

    switch (type) {
        case 'open':
            // Single-key model: the worker uses globalKey set via
            // setGlobalEncryptionKey (see SetEncryptionKeyAsync on the C#
            // side). Open carries no key envelope.
            return await openDatabase(database!);

        case 'setGlobalEncryptionKey':
            // Install the worker-wide key. Every page I/O across every open
            // DB encrypts under it immediately — xRead / xWrite consult
            // getGlobalKey() per top-level operation. Disk-as-unit model:
            // file handles need no invalidation on key swap.
            if (!binaryPayload) {
                throw new Error('setGlobalEncryptionKey requires binaryPayload (VfsKeyHeader)');
            }
            return await setGlobalEncryptionKeyOp(unpackVfsKeyHeader(new Uint8Array(binaryPayload)));

        case 'clearGlobalEncryptionKey':
            // Drop the worker-wide key. Closes open DBs first for page-cache
            // coherence at the session boundary. Idempotent.
            return await clearGlobalEncryptionKeyOp();

        case 'listDatabases':
            // Session.EnterEncryptedAsync / LeaveEncryptedAsync iterate
            // these to encrypt-in-place / decrypt-in-place every DB.
            // Returns bare names (no /databases/ prefix), no journal/WAL
            // siblings, no .vfs-lock.
            return { databases: poolUtil.listDatabases() };

        case 'execute':
            // When binaryPayload is present, blob params carry
            // { __blobOffset, __blobLength } placeholders pointing into the
            // attached buffer instead of Base64 strings in the JSON.
            // convertParametersForBinding reads bytes from binaryPayload.
            return await executeSql(
                database!, sql!, parameters || {},
                binaryPayload ? new Uint8Array(binaryPayload) : undefined);

        case 'close':
            return await closeDatabase(database!);

        case 'exists':
            return await checkDatabaseExists(database!);

        case 'delete':
            return await deleteDatabase(database!);

        case 'rename':
            return await renameDatabase(database!, (data as any).newName);

          case 'importDb':
              if (!binaryPayload) {
                  throw new Error('importDb requires binaryPayload');
              }
              return await importDatabase(
                  database!,
                  new Uint8Array(binaryPayload),
                  (data as any).opaque === true
              );

          case 'verifyImportRekey':
              // Non-destructive AEAD preflight for asymmetric import. Caller
              // validates every envelope file BEFORE wiping the existing
              // pool — "no destroy unless verified" invariant from the
              // 4th-pass Codex audit (24dd02f). Same payload shape as
              // importDbRekey; we just throw away the rekey output.
              if (!binaryPayload) {
                  throw new Error('verifyImportRekey requires binaryPayload');
              }
              return await withImportRekeyEnvelope(
                  new Uint8Array(binaryPayload),
                  (vwk, vdb) => verifyImportRekey(database!, vwk, vdb));

          case 'importDbRekey':
              // Asymmetric import — VfsImportRekeyEnvelope. Worker decrypts
              // under wrapKey + re-encrypts under registered globalKey via
              // rekeySlots, then routes through the opaque-import path so
              // verify-on-write asserts the rekey came out under the
              // recipient's actual disk key. wrapKey is transit-only.
              if (!binaryPayload) {
                  throw new Error('importDbRekey requires binaryPayload');
              }
              logger.info(
                  MODULE_NAME,
                  `[asym-import] importDbRekey: db=${database}, payload=${binaryPayload.byteLength}B`);
              return await withImportRekeyEnvelope(
                  new Uint8Array(binaryPayload),
                  (wrapKey, dbBytes) => {
                      logger.info(
                          MODULE_NAME,
                          `[asym-import] unpacked: wrapKey=${keyFingerprint(wrapKey)} dbBytes=${dbBytes.length}B`);
                      return importDatabaseWithRekey(database!, wrapKey, dbBytes);
                  });

          case 'importDbPlain':
              // Plain-source import onto an Encrypted+Unlocked disk. The
              // payload IS the plain SQLite bytes (no envelope wrapper —
              // there's no transit key to ship). Worker re-encrypts each
              // 4096-byte page under the registered globalKey via rekeySlots
              // and routes through opaque-import so verify-on-write confirms
              // the encrypted slots decrypt under the disk key. Lets a Plain
              // ZIP land on an Encrypted+Unlocked disk while preserving the
              // passkey binding.
              if (!binaryPayload) {
                  throw new Error('importDbPlain requires binaryPayload');
              }
              return await importDatabasePlain(
                  database!,
                  new Uint8Array(binaryPayload));

          case 'exportDb': {
            // Slot-rekey primitive with four flavours selected by `mode`:
            //   verbatim → raw OPFS bytes (slot-format ciphertext or plain pages)
            //   plain    → decrypt under registered K_old → plain pages
            //   rekey    → decrypt under K_old, re-encrypt under caller-supplied
            //              K_new (binaryPayload carries VfsKeyHeader). Source MUST
            //              be encrypted (registered K_old).
            //   encrypt  → encrypt plain source under caller-supplied K_target.
            //              Source MUST be plain (no registered key). Symmetric
            //              with rekey but for the plain→encrypted byte-shuttle
            //              backup/sharing case.
            const mode = (data as any).mode as 'verbatim' | 'plain' | 'rekey' | 'encrypt';
            let newKey: Uint8Array | undefined;
            if (mode === 'rekey' || mode === 'encrypt') {
                if (!binaryPayload) {
                    throw new Error(
                        `exportDb mode='${mode}' requires binaryPayload (VfsKeyHeader for K_new)`);
                }
                newKey = unpackVfsKeyHeader(new Uint8Array(binaryPayload));
                logger.info(
                    MODULE_NAME,
                    `[asym-export] exportDb mode=${mode} db=${database} ` +
                    `newKey=${keyFingerprint(newKey)}`);
            }
            try {
                return await exportDatabase(database!, mode, newKey);
            } finally {
                // newKey is the caller-supplied K_target — wipe before
                // returning regardless of whether export succeeded.
                if (newKey !== undefined) {
                    clearBytes(newKey);
                }
            }
        }

        case 'encryptDb':
            // In-place plain → encrypted: reads OPFS plain pages, re-wraps
            // under the caller-supplied 32-byte K, writes back as encrypted
            // slots. Bytes never leave the worker. Caller must
            // registerEncryptionKey before the next open.
            if (!binaryPayload) {
                throw new Error("encryptDb requires binaryPayload (VfsKeyHeader for K)");
            }
            return await withVfsKeyHeader(
                new Uint8Array(binaryPayload),
                k => encryptDatabaseInPlace(database!, k));

        case 'decryptDb':
            // In-place encrypted → plain: snapshots the registered K,
            // decrypts to plain pages, writes back as plain. Bytes never
            // leave the worker.
            return await decryptDatabaseInPlace(database!);

        case 'importRows':
            if (!binaryPayload) {
                throw new Error('importRows requires binaryPayload (V2 MessagePack)');
            }
            return importRows(database!, new Uint8Array(binaryPayload), data as any);

        case 'deltaExportEncrypted':
            if (!binaryPayload) {
                throw new Error('deltaExportEncrypted requires binaryPayload (CryptoHeader)');
            }
            return await deltaExportEncrypted(database!, new Uint8Array(binaryPayload), data as any);

        case 'deltaImportEncrypted':
            if (!binaryPayload || !binaryHeader) {
                throw new Error('deltaImportEncrypted requires binaryPayload (CryptoHeader) + binaryHeader (ShadowRowGroup)');
            }
            return await deltaImportEncrypted(
                database!,
                new Uint8Array(binaryPayload),
                new Uint8Array(binaryHeader),
                data as any
            );

        case 'bulkRotateKey':
            if (!binaryPayload || !binaryHeader) {
                throw new Error('bulkRotateKey requires binaryPayload (oldCryptoHeader) + binaryHeader (newCryptoHeader)');
            }
            return await bulkRotateKey(
                database!,
                new Uint8Array(binaryPayload),
                new Uint8Array(binaryHeader),
                data as any
            );

        case 'readDiskManifest':
            // Disk-bound passkey manifest read. Walks every DB in the SAHPool,
            // pulls bytes 524..1023 of each header sector, asserts they all
            // match, and parses out the body. When `verifyMac` is true (only
            // valid post-unlock — globalKey present) the HMAC is also checked.
            return await readDiskManifestOp((data as any).verifyMac === true);

        case 'writeDiskManifest':
            // Disk-bound passkey manifest write. binaryPayload carries the
            // (already-MessagePack-serialized) body bytes from C#; globalKey
            // MUST be installed so we can HKDF-derive the manifest MAC key.
            // Writes the same 500-byte region into every DB's header sector.
            if (!binaryPayload) {
                throw new Error('writeDiskManifest requires binaryPayload (manifest body bytes)');
            }
            return await writeDiskManifestOp(new Uint8Array(binaryPayload));

        case 'clearDiskManifest':
            // Zero bytes 524..1023 of every DB's header sector. Used by
            // LeaveEncryptedAsync / ResetDiskAsync when the disk transitions
            // out of Encrypted state. No globalKey requirement (we're erasing,
            // not authenticating).
            return await clearDiskManifestOp();

        default:
            throw new Error(`Unknown request type: ${type}`);
    }
}

async function openDatabase(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const dbPath = `/databases/${dbName}`;

    let db = openDatabases.get(dbName);

    // Single-key model: the VFS's xRead / xWrite consult globalKey per page
    // I/O. C# sets globalKey via SetEncryptionKeyAsync at the session
    // boundary; this open call never carries key material.

    // Check if database needs to be opened
    if (!db) {
        try {
            // Use OpfsSAHPoolDb from the pool utility
            // Wrap in timeout to detect multi-tab lock conflicts
            const openPromise = new Promise<any>((resolve, reject) => {
                try {
                    const database = new poolUtil.OpfsSAHPoolDb(dbPath);
                    resolve(database);
                } catch (error) {
                    reject(error);
                }
            });

            const timeoutPromise = new Promise<any>((_, reject) =>
                setTimeout(() => reject(
                    new Error(`Timeout opening database: ${dbName}`)
                ), 4000)
            );

            db = await Promise.race([openPromise, timeoutPromise]);
            openDatabases.set(dbName, db);
            logger.info(
                MODULE_NAME,
                `✓ Opened database: ${dbName} with OPFS SAHPool${hasGlobalKey() ? ' (encrypted)' : ''}`
            );

            // Debug: Verify database is in OPFS
            if (poolUtil.getFileNames) {
                const filesInOPFS = poolUtil.getFileNames();
                const isInOPFS = filesInOPFS.includes(dbPath);
                logger.debug(MODULE_NAME, `Database ${dbName} in OPFS: ${isInOPFS}, Total files: ${filesInOPFS.length}`);
                if (!isInOPFS) {
                    logger.warn(MODULE_NAME, `WARNING: Database ${dbName} was opened but is not in OPFS file list!`);
                }
            }
        } catch (error) {
            logger.error(MODULE_NAME, `Failed to open database ${dbName}:`, error);
            throw error;
        }
    }

    // Always check if PRAGMAs need to be set (even if database was already open)
    // This handles the case where database was closed and reopened
    if (!pragmasSet.has(dbName)) {
        if (hasGlobalKey()) {
            // Encrypted DBs use the offset-remapping PRF-VFS, which encrypts
            // every file type (main DB, WAL frames, rollback journals, temp)
            // uniformly under the same AEAD envelope. That makes WAL safe
            // on-disk, so we match the plain-DB journal mode and get full
            // crash recovery back.
            //
            // page_size MUST be 4096: the VFS's logical→physical slot math
            // assumes a 4096-byte plaintext block per slot. Any other
            // page_size would desync the slot boundaries on READs.
            db.exec("PRAGMA page_size = 4096;");
            db.exec("PRAGMA locking_mode = exclusive;");
            db.exec("PRAGMA journal_mode = WAL;");
            db.exec("PRAGMA synchronous = FULL;");
            logger.debug(
                MODULE_NAME,
                `Set PRAGMAs for ${dbName} (encrypted: page_size=4096, journal_mode=WAL)`
            );
        } else {
            // Plain DBs: existing behavior unchanged.
            db.exec("PRAGMA locking_mode = exclusive;");
            db.exec("PRAGMA journal_mode = WAL;");
            db.exec("PRAGMA synchronous = FULL;");
            logger.debug(
                MODULE_NAME,
                `Set PRAGMAs for ${dbName} (locking_mode=exclusive, journal_mode=WAL, synchronous=FULL)`
            );
        }
        pragmasSet.add(dbName);

        // Register EF Core scalar and aggregate functions for feature completeness
        // These functions enable full decimal arithmetic and comparison support in EF Core queries
        registerEFCoreFunctions(db, sqlite3);
    }

    return { success: true };
}

/**
 * Disk-as-unit model: install the global encryption key. Page I/O picks up
 * the new key dynamically via getGlobalKey() — there is no per-OFile
 * snapshot to invalidate on the file handle level.
 *
 * Closes every cached DB before swapping, however, for **page-cache
 * coherence at the session boundary**: SQLite caches decrypted plaintext
 * pages in memory after first read, and a key change is conceptually a
 * session boundary — sessions don't share caches. Without this close,
 * plaintext decrypted under K_old would remain readable from cache after
 * we swap to K_new (or vice versa), violating the key-isolation property
 * the encrypted VFS exists to provide.
 *
 * Idempotent: replaces a previously-set globalKey, wiping the old buffer
 * in place. Caller (C#) wipes its envelope copy after the call returns.
 */
async function setGlobalEncryptionKeyOp(key: Uint8Array) {
    for (const dbName of [...openDatabases.keys()]) {
        await closeDatabase(dbName);
    }
    setGlobalKey(key);
    logger.debug(MODULE_NAME, `Installed global encryption key`);
    return { success: true };
}

/**
 * Drop the global encryption key. Same close-pass-for-cache-coherence as
 * {@link setGlobalEncryptionKeyOp} — pages decrypted under K_old must not
 * be served from cache after K_old is gone. Idempotent.
 */
async function clearGlobalEncryptionKeyOp() {
    for (const dbName of [...openDatabases.keys()]) {
        await closeDatabase(dbName);
    }
    clearGlobalKey();
    logger.debug(MODULE_NAME, `Cleared global encryption key`);
    return { success: true };
}

/**
 * Deserialize a VfsKeyHeader (see SqliteWasmBlazor.Services.VfsKeyHeader).
 * Returns just the 32-byte key after version/AAD validation. Throws on an
 * envelope we don't recognize rather than opening the DB with a misparsed
 * key and corrupting pages.
 *
 * Envelope shape (matches MessagePack [Key(n)] on the C# type):
 *   0: version (int)
 *   1: key (bytes, 32)
 *   2: aadVersion (string)
 */
function unpackVfsKeyHeader(headerBytes: Uint8Array): Uint8Array {
    let key: Uint8Array | undefined;
    try {
        const decoded = unpack(headerBytes);
        if (!Array.isArray(decoded) || decoded.length < 2) {
            throw new Error('VfsKeyHeader: invalid MessagePack envelope');
        }
        const [version, decodedKey, aadVersion] = decoded as [number, Uint8Array, string];
        key = decodedKey;
        if (version !== 1) {
            throw new Error(`VfsKeyHeader: unsupported version ${version} (expected 1)`);
        }
        if (!(key instanceof Uint8Array) || key.length !== 32) {
            throw new Error(
                `VfsKeyHeader: key must be a 32-byte Uint8Array (got length=${(key as any)?.length})`
            );
        }
        if (aadVersion !== undefined && aadVersion !== 'v1') {
            throw new Error(
                `VfsKeyHeader: unsupported aadVersion "${aadVersion}" (expected "v1")`
            );
        }

        // Return an owned key buffer. The MessagePack payload and decoded key
        // view are zeroed below, so callers can safely retain the returned key
        // until their own lifecycle finally clears it.
        return new Uint8Array(key);
    } finally {
        if (key instanceof Uint8Array) {
            clearBytes(key);
        }
        clearBytes(headerBytes);
    }
}

/**
 * Deserialize a VfsImportRekeyEnvelope (see C# type of the same name).
 * Returns the wrap key + db bytes as separate buffers. The MessagePack
 * payload and the wrap-key view inside it are zeroed before returning so
 * the only live copy of the wrap key is the owned buffer the caller wipes
 * in finally.
 *
 * Envelope shape (matches MessagePack [Key(n)] on the C# type):
 *   0: version (int)
 *   1: wrapKey (bytes, 32)
 *   2: dbBytes (bytes)
 *   3: aadVersion (string)
 */
function unpackImportRekeyEnvelope(envelopeBytes: Uint8Array): {
    wrapKey: Uint8Array;
    dbBytes: Uint8Array;
} {
    let decodedWrapKey: Uint8Array | undefined;
    try {
        const decoded = unpack(envelopeBytes);
        if (!Array.isArray(decoded) || decoded.length < 3) {
            throw new Error('VfsImportRekeyEnvelope: invalid MessagePack envelope');
        }
        const [version, wk, db, aadVersion] = decoded as [
            number, Uint8Array, Uint8Array, string | undefined,
        ];
        decodedWrapKey = wk;
        if (version !== 1) {
            throw new Error(
                `VfsImportRekeyEnvelope: unsupported version ${version} (expected 1)`);
        }
        if (!(wk instanceof Uint8Array) || wk.length !== 32) {
            throw new Error(
                `VfsImportRekeyEnvelope: wrapKey must be a 32-byte Uint8Array (got length=${(wk as any)?.length})`);
        }
        if (!(db instanceof Uint8Array)) {
            throw new Error('VfsImportRekeyEnvelope: dbBytes must be a Uint8Array');
        }
        if (aadVersion !== undefined && aadVersion !== 'v1') {
            throw new Error(
                `VfsImportRekeyEnvelope: unsupported aadVersion "${aadVersion}" (expected "v1")`);
        }
        // Both fields must be FRESH allocations, not views into envelopeBytes —
        // the finally below clears envelopeBytes and any subarray view would
        // be zeroed in lockstep. msgpackr returns bin fields as subarrays into
        // the source buffer, so wrapping each in `new Uint8Array(...)` lifts
        // them into independent storage that survives the source wipe.
        return { wrapKey: new Uint8Array(wk), dbBytes: new Uint8Array(db) };
    } finally {
        if (decodedWrapKey instanceof Uint8Array) {
            clearBytes(decodedWrapKey);
        }
        clearBytes(envelopeBytes);
    }
}

// Scope helper: unpack a VfsKeyHeader, hand the 32-byte key to `fn`, wipe
// on exit. Use for transient-use sites (encryptDb, rekey/encrypt export).
// Do NOT use when the key's ownership transfers to a longer-lived owner
// (setGlobalKey takes ownership and wipes on its own swap).
async function withVfsKeyHeader<T>(
    headerBytes: Uint8Array,
    fn: (key: Uint8Array) => Promise<T> | T,
): Promise<T> {
    const key = unpackVfsKeyHeader(headerBytes);
    try {
        return await fn(key);
    } finally {
        clearBytes(key);
    }
}

// Scope helper: unpack a VfsImportRekeyEnvelope, hand wrap key + db bytes
// to `fn`, wipe the wrap key on exit. dbBytes are not wiped — they're
// rekey input that the import op consumes and may retain past the
// callback.
async function withImportRekeyEnvelope<T>(
    envelopeBytes: Uint8Array,
    fn: (wrapKey: Uint8Array, dbBytes: Uint8Array) => Promise<T> | T,
): Promise<T> {
    const { wrapKey, dbBytes } = unpackImportRekeyEnvelope(envelopeBytes);
    try {
        return await fn(wrapKey, dbBytes);
    } finally {
        clearBytes(wrapKey);
    }
}

// Get schema info for a table by querying PRAGMA table_info
// Cache key includes database name to prevent collisions when multiple databases
// have tables with the same name but different schemas
function getTableSchema(db: any, dbName: string, tableName: string): Map<string, string> {
    const cacheKey = `${dbName}:${tableName}`;
    if (schemaCache.has(cacheKey)) {
        return schemaCache.get(cacheKey)!;
    }

    const schema = new Map<string, string>();
    try {
        // Query PRAGMA table_info to get column types
        const result = db.exec({
            sql: `PRAGMA table_info("${tableName}")`,
            returnValue: 'resultRows',
            rowMode: 'array'
        });

        // PRAGMA table_info returns: [cid, name, type, notnull, dflt_value, pk]
        for (const row of result) {
            const columnName = row[1] as string;  // name
            const columnType = row[2] as string;  // type
            schema.set(columnName, columnType.toUpperCase());
        }

        schemaCache.set(cacheKey, schema);
    } catch (error) {
        logger.warn(MODULE_NAME, `Failed to load schema for table ${tableName}:`, error);
    }

    return schema;
}

// Extract table name from SELECT statement (simple heuristic)
function extractTableName(sql: string): string | null {
    // Match: SELECT ... FROM "tableName" or FROM tableName
    const match = sql.match(/FROM\s+["']?(\w+)["']?/i);
    return match ? match[1] : null;
}

/**
 * Converts parameters with type metadata for proper SQLite binding
 * Expects parameters in format: { value: any, type: "blob" | "text" | "integer" | "real" | "null" }
 */
function convertParametersForBinding(
    parameters: Record<string, any>,
    binaryPayload?: Uint8Array,
): Record<string, any> {
    const converted: Record<string, any> = {};

    for (const [key, paramData] of Object.entries(parameters)) {
        // Handle new format with type metadata
        if (paramData && typeof paramData === 'object' && 'value' in paramData && 'type' in paramData) {
            const { value, type } = paramData;

            if (value === null || value === undefined) {
                converted[key] = null;
                logger.debug(MODULE_NAME, `[PARAM] ${key}: null`);
            }
            else if (type === 'blob' && binaryPayload && value && typeof value === 'object'
                     && typeof value.__blobOffset === 'number' && typeof value.__blobLength === 'number') {
                // Blob bytes carried in the binary attachment, not Base64.
                // Slice (not subarray-view-passthrough) so SQLite binding owns
                // an independent buffer — binaryPayload's underlying ArrayBuffer
                // may be reused on the next request.
                const offset = value.__blobOffset;
                const length = value.__blobLength;
                const bytes = new Uint8Array(length);
                bytes.set(binaryPayload.subarray(offset, offset + length));
                converted[key] = bytes;
                logger.debug(MODULE_NAME, `[PARAM] ${key}: blob (${length} bytes from binary attachment @ ${offset})`);
            }
            else if (type === 'blob' && typeof value === 'string') {
                // Legacy fallback — Base64-encoded blob in the JSON message.
                try {
                    const binaryString = atob(value);
                    const bytes = new Uint8Array(binaryString.length);
                    for (let i = 0; i < binaryString.length; i++) {
                        bytes[i] = binaryString.charCodeAt(i);
                    }
                    converted[key] = bytes;
                    logger.debug(MODULE_NAME, `[PARAM] ${key}: blob (${bytes.length} bytes from base64)`);
                } catch (e) {
                    logger.error(MODULE_NAME, `[PARAM] Failed to decode blob ${key}:`, e);
                    converted[key] = value;
                }
            }
            else {
                // For text, integer, real - use value as-is
                converted[key] = value;
                logger.debug(MODULE_NAME, `[PARAM] ${key}: ${type} = ${typeof value === 'string' && value.length > 50 ? value.substring(0, 50) + '...' : value}`);
            }
        }
        else {
            // Fallback for old format (backwards compatibility)
            logger.warn(MODULE_NAME, `[PARAM] ${key}: using legacy format (no type metadata)`);
            converted[key] = paramData;
        }
    }

    return converted;
}

async function executeSql(
    dbName: string, sql: string,
    parameters: Record<string, any>,
    binaryPayload?: Uint8Array,
) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    try {
        logger.debug(MODULE_NAME, 'Executing SQL:', sql.substring(0, 100));

        // Convert parameters with type metadata for proper SQLite binding.
        // binaryPayload (if present) carries blob param bytes — see
        // convertParametersForBinding for the __blobOffset/__blobLength
        // placeholder shape.
        const convertedParams = convertParametersForBinding(parameters, binaryPayload);

        // Execute SQL - use returnValue to get the result
        const result = db.exec({
            sql: sql,
            bind: Object.keys(convertedParams).length > 0 ? convertedParams : undefined,
            returnValue: 'resultRows',
            rowMode: 'array'
        });

        logger.debug(MODULE_NAME, 'SQL executed successfully, rows:', result?.length || 0);

        // Extract column metadata if there are results
        let columnNames: string[] = [];
        let columnTypes: string[] = [];

        if (result && result.length > 0) {
            const stmt = db.prepare(sql);
            try {
                const colCount = stmt.columnCount;

                // Try to get schema from table (for SELECT queries)
                let tableSchema: Map<string, string> | null = null;
                if (sql.trim().toUpperCase().startsWith('SELECT')) {
                    const tableName = extractTableName(sql);
                    if (tableName) {
                        tableSchema = getTableSchema(db, dbName, tableName);
                    }
                }

                for (let i = 0; i < colCount; i++) {
                    const colName = stmt.getColumnName(i);
                    columnNames.push(colName);

                    // Use declared type from schema if available
                    let declaredType = tableSchema?.get(colName);

                    // Normalize declared type to SQLite affinity
                    let inferredType = 'TEXT';
                    if (declaredType) {
                        const typeUpper = declaredType.toUpperCase();
                        if (typeUpper.includes('INT')) {
                            inferredType = 'INTEGER';
                        } else if (typeUpper.includes('REAL') || typeUpper.includes('DOUBLE') || typeUpper.includes('FLOAT')) {
                            inferredType = 'REAL';
                        } else if (typeUpper.includes('BLOB')) {
                            inferredType = 'BLOB';
                        } else {
                            inferredType = 'TEXT';
                        }
                    } else if (result.length > 0 && result[0][i] !== null) {
                        // Fallback to value-based inference if no schema available
                        const value = result[0][i];

                        if (typeof value === 'number') {
                            inferredType = Number.isInteger(value) ? 'INTEGER' : 'REAL';
                        } else if (typeof value === 'bigint') {
                            inferredType = 'INTEGER';
                        } else if (typeof value === 'boolean') {
                            inferredType = 'INTEGER';
                        } else if (value instanceof Uint8Array || ArrayBuffer.isView(value)) {
                            inferredType = 'BLOB';
                        }
                    }
                    columnTypes.push(inferredType);
                }
            } finally {
                stmt.finalize();
            }
        }

        // Get changes and last insert ID for non-SELECT queries
        let rowsAffected = 0;
        let lastInsertId = 0;

        if (sql.trim().toUpperCase().startsWith('INSERT') ||
            sql.trim().toUpperCase().startsWith('UPDATE') ||
            sql.trim().toUpperCase().startsWith('DELETE') ||
            sql.trim().toUpperCase().startsWith('CREATE')) {

            // Check if statement has RETURNING clause
            // When RETURNING is used, db.changes() doesn't work correctly because
            // SQLite treats it as a SELECT-like operation
            const hasReturning = sql.toUpperCase().includes('RETURNING');

            if (hasReturning && result && result.length > 0) {
                // For UPDATE/DELETE with RETURNING, the presence of a result row means success
                rowsAffected = result.length;
            }
            else {
                // For INSERT without RETURNING, or any statement without RETURNING
                rowsAffected = db.changes();
            }

            lastInsertId = db.lastInsertRowId;
        }

        const response = {
            columnNames,
            columnTypes,
            typedRows: {
                types: columnTypes,
                data: convertBigInt(result || [])
            },
            rowsAffected,
            lastInsertId: Number(lastInsertId)
        };

        return pack(response);
    } catch (error) {
        logger.error(MODULE_NAME, 'SQL execution failed:', error);
        logger.error(MODULE_NAME, 'SQL:', sql);
        throw error;
    }
}

async function closeDatabase(dbName: string) {
    const db = openDatabases.get(dbName);
    if (db) {
        db.close();
        openDatabases.delete(dbName);
        pragmasSet.delete(dbName); // Clear PRAGMA tracking when database is closed
        // Single-key model: globalKey is worker-wide and survives DB close.
        // Caller (C#) controls its lifecycle via SetEncryptionKeyAsync /
        // ClearEncryptionKeyAsync at session boundaries.
        logger.info(MODULE_NAME, `Closed database: ${dbName}`);
    }
    return { success: true };
}

async function checkDatabaseExists(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        // Check if database is currently open
        if (openDatabases.has(dbName)) {
            return { rowsAffected: 1 };  // exists
        }

        // Check if database file exists in OPFS SAHPool
        const dbPath = `/databases/${dbName}`;

        // Try to check file existence using poolUtil's file list
        // The poolUtil exposes information about stored databases
        if (poolUtil.getFileNames) {
            const files = await poolUtil.getFileNames();
            const exists = files.includes(dbPath);
            return { rowsAffected: exists ? 1 : 0 };
        }

        // Fallback: try to open database to check if it exists
        try {
            const testDb = new poolUtil.OpfsSAHPoolDb(dbPath);
            testDb.close();
            return { rowsAffected: 1 };  // exists
        } catch {
            return { rowsAffected: 0 };  // doesn't exist
        }
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to check database ${dbName}:`, error);
        // On error, assume it doesn't exist
        return { rowsAffected: 0 };
    }
}

async function deleteDatabase(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        // Close database if open
        await closeDatabase(dbName);

        // Delete database file from OPFS SAHPool
        const dbPath = `/databases/${dbName}`;

        // Use unlink to delete a specific database file (not wipeFiles which deletes ALL databases!)
        if (poolUtil.unlink) {
            const deleted = poolUtil.unlink(dbPath);
            if (deleted) {
                logger.info(MODULE_NAME, `✓ Deleted database: ${dbName}`);
            } else {
                logger.debug(MODULE_NAME, `Database ${dbName} was not in OPFS (already deleted or never created)`);
            }
        } else {
            logger.warn(MODULE_NAME, `unlink not available, database may persist`);
        }

        return { success: true };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to delete database ${dbName}:`, error);
        throw error;
    }
}

async function renameDatabase(oldName: string, newName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        const oldPath = `/databases/${oldName}`;
        const newPath = `/databases/${newName}`;

        logger.info(MODULE_NAME, `Renaming database from ${oldName} to ${newName}`);

        // Debug: Show what files are in OPFS before rename
        if (poolUtil.getFileNames) {
            const filesInOPFS = poolUtil.getFileNames();
            logger.debug(MODULE_NAME, `Files currently in OPFS (${filesInOPFS.length}):`, filesInOPFS);
            logger.debug(MODULE_NAME, `Looking for: ${oldPath}`);
            logger.debug(MODULE_NAME, `File exists in OPFS: ${filesInOPFS.includes(oldPath)}`);
        }

        // Ensure both databases are closed before rename
        logger.debug(MODULE_NAME, `Ensuring databases are closed before rename operation`);
        await closeDatabase(oldName);
        await closeDatabase(newName);

        // Use native OPFS SAHPool renameFile() - updates metadata mapping without copying file data
        logger.debug(MODULE_NAME, `Renaming database file in OPFS: ${oldPath} -> ${newPath}`);

        try {
            poolUtil.renameFile(oldPath, newPath);
            logger.info(MODULE_NAME, `✓ Successfully renamed database from ${oldName} to ${newName} (metadata-only, no file copy)`);

            // Debug: Verify rename worked
            if (poolUtil.getFileNames) {
                const filesAfterRename = poolUtil.getFileNames();
                logger.debug(MODULE_NAME, `Files after rename:`, filesAfterRename);
            }
        } catch (renameError) {
            logger.error(MODULE_NAME, `Failed to rename database:`, renameError);
            throw new Error(`Failed to rename database from ${oldName} to ${newName}: ${renameError}`);
        }

        return { success: true };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to rename database from ${oldName} to ${newName}:`, error);
        throw error;
    }
}

async function importDatabase(dbName: string, data: Uint8Array, opaque = false) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        logger.info(
            MODULE_NAME,
            `Importing database ${dbName} (${data.length} bytes${opaque ? ', opaque' : ''})`
        );

        const dbPath = `/databases/${dbName}`;

        // For opaque (encrypted) imports, refuse to overwrite an existing DB.
        // Rolling back a partial overwrite would require a backup-and-restore
        // dance; the design memo's policy is "caller must wipe first" instead.
        // Plain imports keep their overwrite semantics for back-compat with
        // the existing plain-DB import test suite.
        if (opaque) {
            const fileNames: string[] = poolUtil.getFileNames();
            if (fileNames.includes(dbPath)) {
                logger.warn(
                    MODULE_NAME,
                    `Refused opaque import of ${dbName}: existing DB at ${dbPath}; caller must wipe first`,
                );
                // VfsImportResult.EXISTING_DB_REFUSED = 2
                return { rowsAffected: 2 };
            }
        }

        // Close database if open (SAHPool requirement). Note: for encrypted
        // paths this ALSO clears the key registry entry, so a subsequent
        // opaque import cannot be detected via isEncryptedPath — the opaque
        // signal must flow explicitly through the import call.
        await closeDatabase(dbName);

        // Import the raw database file into OPFS SAHPool. When opaque=true,
        // the fork skips the 'SQLite format 3' header check and the byte-18
        // WAL-mode patch, which would corrupt an AEAD tag for encrypted DBs.
        poolUtil.importDb(dbPath, data, opaque);

        // Verify-on-write: when an encryption key is registered for this
        // path, AEAD-test slot 0 of the freshly written DB. On WrongKey
        // unlink the file so the failed import leaves no half-written DB
        // behind. This catches both corrupted ciphertext and recipient-side
        // key mismatches at write time, instead of waiting for the first
        // SQLite read to fail.
        if (opaque && hasGlobalKey()) {
            const verify = poolUtil.verifyEncryptionKey(dbPath);
            if (verify === 'wrongKey') {
                poolUtil.unlink(dbPath);
                logger.warn(
                    MODULE_NAME,
                    `Verify-on-write rejected import of ${dbName}: AEAD failed on slot 0; rolled back`,
                );
                // VfsImportResult.WRONG_KEY = 1
                return { rowsAffected: 1 };
            }
            logger.debug(
                MODULE_NAME,
                `Verify-on-write OK for ${dbName} (slot 0: ${verify})`,
            );
        }

        logger.info(MODULE_NAME, `✓ Imported database: ${dbName} (${data.length} bytes)`);

        // VfsImportResult.OK = 0
        return { rowsAffected: 0 };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to import database ${dbName}:`, error);
        throw error;
    }
}

/**
 * Diagnostic-only key marker. Keep logs useful for spotting whether a key
 * was present without emitting even a prefix of secret key material.
 */
function keyFingerprint(key: Uint8Array): string {
    return `<redacted:${key.length}B>`;
}

/**
 * Non-destructive AEAD preflight for asymmetric (v2) import. Runs the
 * decrypt half of rekeySlots against the envelope's page ciphertext under
 * the caller's wrap key, discards the plaintext output, returns 0 on
 * success or 1 on AEAD-tag failure. No OPFS write. Caller validates every
 * file in the envelope BEFORE wiping the existing pool so a corrupt
 * envelope can never destroy data part-way through a multi-file import.
 * Closes the same audit invariant the v1 verifyEncryptedImportBytes did
 * (24dd02f), adapted to the K_wrap-under-AEAD shape.
 */
function verifyImportRekey(
    dbName: string,
    wrapKey: Uint8Array,
    dbBytes: Uint8Array,
) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    const dbPath = `/databases/${dbName}`;
    let plain: Uint8Array | undefined;
    try {
        // sourceKey = wrapKey, targetKey = undefined → decrypt-to-plain.
        // Throws on any slot's AEAD tag failure; bytes never touch OPFS.
        plain = rekeySlots(dbBytes, dbPath, wrapKey, undefined);
        return { rowsAffected: 0 };
    } catch {
        // VfsImportResult.WRONG_KEY = 1
        return { rowsAffected: 1 };
    } finally {
        if (plain !== undefined) {
            clearBytes(plain);
        }
    }
}

/**
 * Asymmetric-import primitive: decrypt incoming page ciphertext under the
 * caller-supplied per-export wrap key, re-encrypt under the registered
 * globalKey, then route through the standard opaque-import path. The
 * rekeyed bytes are persisted under the recipient's actual disk key, so
 * verify-on-write and every subsequent open can use the unchanged
 * globalKey. The wrap key is transit-only — it never lands on disk and
 * the caller wipes it as part of the postMessage envelope cleanup.
 *
 * AAD binds the bare DB name, so the recipient must import under the
 * same name the sender exported from. Mismatch surfaces as a verify
 * failure inside rekeySlots' decrypt step, before any OPFS write.
 */
async function importDatabaseWithRekey(
    dbName: string,
    wrapKey: Uint8Array,
    dbBytes: Uint8Array,
) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    const globalKey = snapshotGlobalKey();
    if (globalKey === undefined) {
        throw new Error(
            `importDbRekey rejected for ${dbName}: no globalKey registered. ` +
            `Recipient disk must be Encrypted+Unlocked under their own VFS key ` +
            `before an asymmetric envelope can be rekeyed onto it.`);
    }
    let rekeyed: Uint8Array | undefined;
    try {
        const dbPath = `/databases/${dbName}`;
        logger.info(
            MODULE_NAME,
            `[asym-import] rekey-in: dbPath=${dbPath} ` +
            `wrapKey=${keyFingerprint(wrapKey)} ` +
            `globalKey=${keyFingerprint(globalKey)} ` +
            `dbBytes=${dbBytes.length}B`);
        // rekeySlots: decrypt under wrapKey (sourceKey), re-encrypt under
        // globalKey (targetKey). Same primitive used for ExportDatabaseAsync
        // mode='rekey', just with the source/target keys swapped.
        rekeyed = rekeySlots(dbBytes, dbPath, wrapKey, globalKey);
        logger.info(
            MODULE_NAME,
            `[asym-import] rekey-out: rekeyed=${rekeyed.length}B`);
        return await importDatabase(dbName, rekeyed, /* opaque */ true);
    } finally {
        if (rekeyed !== undefined) {
            // rekeyed bytes are ciphertext under globalKey — not strictly
            // sensitive after the importDb call has committed them — but
            // we wipe the buffer anyway since it's our owned allocation.
            clearBytes(rekeyed);
        }
        clearBytes(globalKey);
    }
}

/**
 * Plain-source import primitive: take plain SQLite pages (4096 B each) and
 * re-encrypt every page under the registered globalKey via rekeySlots before
 * routing through the standard opaque-import path. Mirror of
 * importDatabaseWithRekey minus the source-decrypt step (no wrapKey to
 * unwrap — sourceKey is undefined so rekeySlots aliases the input bytes
 * as the per-slot plaintext). Lets a Plain ZIP entry land on an
 * Encrypted+Unlocked disk while preserving the passkey binding: globalKey,
 * manifest, and credentialId all survive untouched.
 */
async function importDatabasePlain(
    dbName: string,
    plainBytes: Uint8Array,
) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    const globalKey = snapshotGlobalKey();
    if (globalKey === undefined) {
        throw new Error(
            `importDbPlain rejected for ${dbName}: no globalKey registered. ` +
            `Disk must be Encrypted+Unlocked before plain bytes can be ` +
            `re-encrypted under the disk key.`);
    }
    let rekeyed: Uint8Array | undefined;
    try {
        const dbPath = `/databases/${dbName}`;
        logger.info(
            MODULE_NAME,
            `[plain-import] rekey-in: dbPath=${dbPath} ` +
            `globalKey=${keyFingerprint(globalKey)} ` +
            `plainBytes=${plainBytes.length}B`);
        // sourceKey=undefined → input is plain SQLite pages; rekeySlots
        // aliases each 4096-byte slot as plaintext and emits 4124-byte
        // encrypted slots under globalKey. AAD binds dbPath + slotIndex,
        // matching the VFS read path's per-page AAD.
        rekeyed = rekeySlots(plainBytes, dbPath, undefined, globalKey);
        logger.info(
            MODULE_NAME,
            `[plain-import] rekey-out: rekeyed=${rekeyed.length}B`);
        return await importDatabase(dbName, rekeyed, /* opaque */ true);
    } finally {
        if (rekeyed !== undefined) {
            clearBytes(rekeyed);
        }
        clearBytes(globalKey);
    }
}

/**
 * Preflight an encrypted whole-disk import before the C# side deletes the
 * existing pool. The AAD binds every slot to its final `/databases/{name}`
 * path, so validation must use the real target path rather than a staging
 * filename. Returns the same rowsAffected code family as importDatabase:
 * 0 = OK, 1 = WRONG_KEY.
 */
/**
 * Walk every DB in the SAHPool, read its 500-byte manifest region, and
 * report a single pool-wide state. The disk-as-unit invariant requires
 * every DB to carry the same manifest bytes — any mismatch surfaces as
 * `'mismatch'` so the caller can refuse to proceed under a corrupted disk.
 *
 * `verifyMac=true` triggers the HMAC check (only legal post-unlock with
 * globalKey installed). Pre-unlock callers leave it false and rely on the
 * AEAD decrypt of slot 0 (deeper in the I/O path) for cryptographic
 * tamper-evidence.
 */
/**
 * Slot-rekey primitive — the single export entry point. Always closes the
 * DB first for a consistent SAH snapshot, then post-processes the raw bytes
 * according to mode:
 *   verbatim → return raw bytes (plain pages or slot-format ciphertext as-is)
 *   plain    → rekeySlots(raw, sourceKey, undefined) → plain SQLite pages
 *   rekey    → rekeySlots(raw, sourceKey, newKey)    → slot ciphertext under K_new
 *   encrypt  → rekeySlots(raw, undefined, newKey)    → slot ciphertext under K_new
 *               from a plain source (no registered K_old). Symmetric to rekey
 *               but with the opposite source-side precondition.
 *
 * AAD binds dbPath, so REKEY / ENCRYPT output must be re-imported to the
 * same DB name.
 */
async function exportDatabase(
    dbName: string,
    mode: 'verbatim' | 'plain' | 'rekey' | 'encrypt',
    newKey?: Uint8Array,
) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const dbPath = `/databases/${dbName}`;

    // Up-front precondition checks before we snapshot any key material —
    // failure path then has nothing to clear that wouldn't be cleared
    // anyway by the finally below.
    if (mode === 'encrypt' && hasGlobalKey()) {
        throw new Error(
            `exportDb mode='encrypt' rejected for ${dbName}: a key is already registered for this path; use mode='rekey' to re-encrypt under a different key.`,
        );
    }
    if ((mode === 'plain' || mode === 'rekey') && !hasGlobalKey()) {
        // Audit fix: REKEY / PLAIN against a plain source would silently
        // run rekeySlots(raw, undefined, ...) and treat raw as plain
        // pages. For REKEY that means plain → encrypted, which is the
        // ENCRYPT mode's job; surfacing the wrong mode here forces
        // callers to be explicit. For PLAIN it's just a no-op verbatim
        // export — same outcome — but rejecting here keeps preconditions
        // symmetric with REKEY and surfaces caller bugs early.
        throw new Error(
            `exportDb mode='${mode}' rejected for ${dbName}: no key registered for this path; ` +
            `use mode='verbatim' for plain DBs or mode='encrypt' to encrypt a plain source.`,
        );
    }

    // verbatim / encrypt do not need a source key (the latter assumes
    // plain input). plain / rekey decrypt slot bytes under K_old —
    // snapshot before closeDatabase clears the registry.
    const sourceKey = (mode === 'verbatim' || mode === 'encrypt')
        ? undefined
        : snapshotGlobalKey();

    let raw: Uint8Array | null = null;

    try {
        await closeDatabase(dbName);

        raw = poolUtil.exportFile(dbPath);

        if (mode === 'verbatim') {
            logger.info(MODULE_NAME, `✓ Exported verbatim ${dbName}: ${raw!.length}B`);
            // Return raw directly; not sensitive in verbatim mode (caller
            // gets whatever was on OPFS — encrypted or plain — and would
            // expect to retain ownership).
            const out = raw;
            raw = null;
            return { rawBinary: true, data: out };
        }

        // Shape validation before we hand bytes to rekeySlots — keeps a
        // mode/file-shape mismatch (e.g. ENCRYPT against a real
        // encrypted-at-rest file after registry loss) from corrupting
        // the output.
        const expectedSourceSlot = (mode === 'encrypt') ? PLAIN_SLOT_SIZE : ENCRYPTED_SLOT_SIZE;
        if (raw!.length === 0 || raw!.length % expectedSourceSlot !== 0) {
            throw new Error(
                `exportDb mode='${mode}' rejected for ${dbName}: file length ${raw!.length} is ` +
                `not a non-zero multiple of expected source slot size ${expectedSourceSlot}.`,
            );
        }

        // Length-only is insufficient for plain-source paths because
        // a 1024-page encrypted DB and a 1031-page plain DB share byte
        // length. Verify the SQLite magic header so an encrypted-at-rest
        // file that happens to divide by 4096 still gets rejected.
        if (mode === 'encrypt' && !hasSqliteMagicHeader(raw!)) {
            throw new Error(
                `exportDb mode='encrypt' rejected for ${dbName}: file does not start with the ` +
                `SQLite magic header — refusing to treat ciphertext as plain pages.`,
            );
        }

        const targetKey = (mode === 'rekey' || mode === 'encrypt') ? newKey : undefined;
        const out = rekeySlots(raw!, dbPath, sourceKey, targetKey);

        logger.info(
            MODULE_NAME,
            `✓ Exported ${mode} ${dbName}: ${raw!.length}B → ${out.length}B`,
        );

        return { rawBinary: true, data: out };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to export ${mode} ${dbName}:`, error);
        throw error;
    } finally {
        // ENCRYPT: raw is plain SQLite pages — sensitive.
        // PLAIN: raw is encrypted ciphertext — not sensitive, but the
        //        rekeySlots output IS plaintext returned to the caller
        //        (caller-owned, can't clear here).
        // REKEY: raw is encrypted ciphertext — not sensitive.
        // Clearing raw unconditionally for non-verbatim is the safer
        // default; it costs one fill per export.
        if (raw !== null) {
            clearBytes(raw);
        }
        // sourceKey snapshot is a fresh copy — clear it so K_old doesn't
        // linger past export, regardless of which branch we took.
        if (sourceKey !== undefined) {
            clearBytes(sourceKey);
        }
        // The caller-supplied newKey came from unpackVfsKeyHeader; its
        // lifetime is owned by the case-block that called us. Cleared
        // there to keep ownership clear.
    }
}

/**
 * In-place plain → encrypted transition. Reads the OPFS file as plain SQLite
 * pages, re-wraps every page under the caller-supplied 32-byte key via
 * rekeySlots, unlinks the existing file, and writes the encrypted slots
 * back to the same path via the opaque importDb path. Bytes never leave
 * the worker — symmetric to ExportDatabaseAsync(REKEY) but local-only.
 *
 * Caller responsibility: no key must be registered for this path before
 * the call (the function rejects otherwise) and the caller must
 * registerEncryptionKey afterwards before opening — the registry is
 * cleared by closeDatabase below.
 */
/**
 * Slot-size constants for shape validation. Plain SQLite pages are 4096
 * bytes; PRF-VFS encrypted slots are 4124 bytes (4096 ciphertext + 12
 * nonce + 16 tag). A correctly-shaped source for a given mode must be
 * an integer multiple of the corresponding slot size.
 *
 * Length-only validation has a known false-positive:
 *   1024 * 4124 = 4222976 = 1031 * 4096
 * — i.e. an encrypted DB of 1024 pages and a plain DB of 1031 pages
 * have the same byte length. Plain-source paths (ENCRYPT mode +
 * encryptDb) must additionally check the 16-byte SQLite magic header
 * to refuse an encrypted-at-rest source that happens to divide evenly.
 */
const PLAIN_SLOT_SIZE = 4096;
const ENCRYPTED_SLOT_SIZE = 4124;

/**
 * "SQLite format 3\0" — the canonical 16-byte header at the start of every
 * SQLite database file (per https://sqlite.org/fileformat.html §1.3). For
 * an encrypted slot-format file, slot 0 starts with ChaCha20-Poly1305
 * ciphertext, so the probability of accidentally matching this exact
 * sequence is ~2^-128 — strong enough to rule out a real encrypted DB.
 */
const SQLITE_MAGIC_HEADER = Uint8Array.from([
    0x53, 0x51, 0x4c, 0x69, 0x74, 0x65, 0x20, 0x66,  // "SQLite f"
    0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x33, 0x00,  // "ormat 3\0"
]);

function hasSqliteMagicHeader(bytes: Uint8Array): boolean {
    if (bytes.length < SQLITE_MAGIC_HEADER.length) {
        return false;
    }
    for (let i = 0; i < SQLITE_MAGIC_HEADER.length; i++) {
        if (bytes[i] !== SQLITE_MAGIC_HEADER[i]) {
            return false;
        }
    }
    return true;
}

/**
 * Atomic-ish OPFS file replacement using SAHPool's metadata-only
 * renameFile. Steps: write new bytes to a temp slot, rename original
 * aside as a backup, rename temp into the original's place, unlink
 * backup. On any failure we attempt to roll back so the original
 * survives — the only way the original is destroyed is a successful
 * rekey followed by a successful renameFile pair, which is the
 * intended outcome.
 *
 * Caller responsibility: the worker has already closed the DB and is
 * holding the raw input bytes / new bytes. This helper only touches
 * OPFS.
 */
function replaceOpfsFileAtomically(
    dbPath: string,
    newBytes: Uint8Array,
    opaque: boolean,
) {
    const tempPath = `${dbPath}.rekey-tmp`;
    const backupPath = `${dbPath}.rekey-bak`;

    // Defensive: clean up leftovers from any prior crashed attempt
    // before starting. unlink is no-op when the file doesn't exist.
    const before: string[] = poolUtil.getFileNames();
    for (const stale of [tempPath, backupPath]) {
        if (before.includes(stale)) {
            try { poolUtil.unlink(stale); } catch { /* best-effort */ }
        }
    }

    let tempWritten = false;
    let originalRenamed = false;
    try {
        // 1. Write new bytes to a temp slot. importDb performs basic
        //    well-formedness checks (and verify-on-write when the path
        //    has a registered key — note the temp path doesn't, so
        //    the verify-on-write branch doesn't fire here regardless of
        //    `opaque`).
        poolUtil.importDb(tempPath, newBytes, opaque);
        tempWritten = true;

        // 2. Move the original aside as a backup so the original path
        //    is free for the temp rename.
        poolUtil.renameFile(dbPath, backupPath);
        originalRenamed = true;

        // 3. Promote temp into the original's place. SAHPool renameFile
        //    is metadata-only — no file copy.
        poolUtil.renameFile(tempPath, dbPath);
        tempWritten = false;

        // 4. Cleanup the backup; original is now the new content.
        try { poolUtil.unlink(backupPath); } catch (e) {
            // Step 4 failure is non-fatal: the original path holds the
            // intended new content; the backup is orphaned but harmless
            // (subsequent in-place ops clean it up at start).
            logger.warn(MODULE_NAME, `replaceOpfsFileAtomically: cleanup of ${backupPath} failed`, e);
        }
        originalRenamed = false;
    } catch (error) {
        // Roll back as far as we got. Order matters:
        //   - If originalRenamed: backup holds the original; move it
        //     back so dbPath points at the unmodified file.
        //   - If tempWritten: temp slot has data we no longer want.
        if (originalRenamed) {
            try { poolUtil.renameFile(backupPath, dbPath); } catch (rollbackErr) {
                logger.error(
                    MODULE_NAME,
                    `replaceOpfsFileAtomically: rollback rename ${backupPath}→${dbPath} failed; ` +
                    `original DB is at ${backupPath}, manual recovery required`,
                    rollbackErr,
                );
            }
        }
        if (tempWritten) {
            try { poolUtil.unlink(tempPath); } catch { /* best-effort */ }
        }
        throw error;
    }
}

async function encryptDatabaseInPlace(dbName: string, key: Uint8Array) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    if (key.length !== 32) {
        throw new Error(`encryptDb: key must be exactly 32 bytes, got ${key.length}`);
    }

    const dbPath = `/databases/${dbName}`;

    if (hasGlobalKey()) {
        throw new Error(
            `encryptDb rejected for ${dbName}: a key is already registered; use rekey-export ceremony to re-encrypt under a different key.`,
        );
    }

    const fileNames: string[] = poolUtil.getFileNames();
    if (!fileNames.includes(dbPath)) {
        throw new Error(`encryptDb: no existing DB at ${dbPath}`);
    }

    let raw: Uint8Array | null = null;
    let encrypted: Uint8Array | null = null;
    try {
        await closeDatabase(dbName);

        raw = poolUtil.exportFile(dbPath);

        // Shape check: source must be plain SQLite pages. The registry
        // says no key, but a real encrypted-at-rest file after a registry
        // loss would still be slot-format ciphertext (4124-byte slots).
        // Reject before rekeySlots so we can't accidentally treat the
        // ciphertext as plain pages and corrupt it.
        if (raw!.length === 0 || raw!.length % PLAIN_SLOT_SIZE !== 0) {
            throw new Error(
                `encryptDb: ${dbName} length ${raw!.length} is not a non-zero multiple of ` +
                `the plain page size ${PLAIN_SLOT_SIZE}; refusing to encrypt a non-plain source.`,
            );
        }

        // Length is necessary but not sufficient — 1024 encrypted slots
        // and 1031 plain pages have the same byte length. Verify the
        // SQLite magic header so we can't misclassify ciphertext.
        if (!hasSqliteMagicHeader(raw!)) {
            throw new Error(
                `encryptDb: ${dbName} does not start with the SQLite magic header — ` +
                `refusing to treat ciphertext as plain pages.`,
            );
        }

        encrypted = rekeySlots(raw!, dbPath, undefined, key);

        // Non-destructive replace: temp-write + double-rename means the
        // original survives any failure inside replaceOpfsFileAtomically.
        replaceOpfsFileAtomically(dbPath, encrypted, /* opaque */ true);

        logger.info(
            MODULE_NAME,
            `✓ Encrypted in place ${dbName}: ${raw!.length}B → ${encrypted.length}B`,
        );

        return { rowsAffected: 0 };
    } finally {
        // raw is plain SQLite pages from OPFS — sensitive plaintext.
        if (raw !== null) {
            clearBytes(raw);
        }
        // encrypted is ciphertext (post-write) — not sensitive, but
        // clearing it costs almost nothing and keeps the GC heap clean.
        if (encrypted !== null) {
            clearBytes(encrypted);
        }
    }
}

/**
 * In-place encrypted → plain transition. Snapshots the registered key,
 * reads encrypted slots, decrypts to plain pages via rekeySlots, and
 * writes the plain pages back via the atomic-replace helper. Bytes never
 * leave the worker. The caller need not call ClearEncryptionKey
 * separately — this method ALWAYS clears the registry in finally so the
 * post-state reflects the on-disk reality (plain).
 */
async function decryptDatabaseInPlace(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const dbPath = `/databases/${dbName}`;

    if (!hasGlobalKey()) {
        throw new Error(
            `decryptDb rejected for ${dbName}: no key registered for this path; nothing to decrypt.`,
        );
    }

    const fileNames: string[] = poolUtil.getFileNames();
    if (!fileNames.includes(dbPath)) {
        throw new Error(`decryptDb: no existing DB at ${dbPath}`);
    }

    // Single-key model: source K is the worker-wide globalKey (the
    // hasGlobalKey() check above passed). Snapshot it as a fresh copy so
    // the wipe below leaves the original intact — caller controls
    // globalKey lifecycle via Set/Clear.
    const sourceKey = snapshotGlobalKey();
    if (sourceKey === undefined) {
        // Should be unreachable given hasGlobalKey above, but defensive.
        throw new Error(`decryptDb: globalKey not set but hasGlobalKey returned true for ${dbName}`);
    }

    let plain: Uint8Array | null = null;
    let raw: Uint8Array | null = null;
    try {
        await closeDatabase(dbName);

        raw = poolUtil.exportFile(dbPath);

        // Shape check: source must be slot-format ciphertext.
        if (raw!.length === 0 || raw!.length % ENCRYPTED_SLOT_SIZE !== 0) {
            throw new Error(
                `decryptDb: ${dbName} length ${raw!.length} is not a non-zero multiple of ` +
                `the encrypted slot size ${ENCRYPTED_SLOT_SIZE}; registry says encrypted but ` +
                `the file shape says plain — refusing to decrypt a non-encrypted source.`,
            );
        }

        plain = rekeySlots(raw!, dbPath, sourceKey, undefined);

        replaceOpfsFileAtomically(dbPath, plain!, /* opaque */ false);

        logger.info(
            MODULE_NAME,
            `✓ Decrypted in place ${dbName}: ${raw!.length}B → ${plain!.length}B`,
        );

        return { rowsAffected: 0 };
    } finally {
        // Single-key model: the on-disk DB is now plain, but globalKey may
        // still be set (caller-controlled). Caller is responsible for
        // dropping globalKey via ClearEncryptionKeyAsync if the worker
        // should be in plain mode after this op.
        // sourceKey is the fresh snapshot — wipe so K_old doesn't
        // linger past the operation.
        clearBytes(sourceKey);
        // raw is encrypted ciphertext from OPFS — not a secret.
        if (raw !== null) {
            clearBytes(raw);
        }
        // plain is the decrypted intermediate — file is now plain on
        // OPFS, but the in-memory buffer is a copy that should be wiped.
        if (plain !== null) {
            clearBytes(plain);
        }
    }
}

/**
 * Plain (non-encrypted) row import from V2 MessagePack payload.
 * Used for seeding, initial data load, test-data generation.
 *
 * DB-agnostic: column metadata comes from the payload header itself
 * (name, sqlType, csharpType per column), which the C# side builds from
 * the DTO via reflection. No dependency on _column_registry, so this
 * works on any open database whose target table matches the header
 * column list — INSERT names columns explicitly, so SQLite handles the
 * rest.
 */
function importRows(dbName: string, payload: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const objects = bigIntUnpackr.unpackMultiple(payload);
    if (objects.length < 1) {
        throw new Error('importRows: empty payload');
    }

    const header = objects[0] as BulkInsertHeader;
    const rows = objects.slice(1) as any[][];
    const conflictStrategy = metadata.conflictStrategy ?? header[6] ?? 0;

    return bulkInsertRows(db, header, rows, conflictStrategy, 'importRows');
}

// Bulk import/export and crypto operations are in separate modules:
// - bulk-ops.ts: MessagePack format, prepared statement loop
// - crypto-delta.ts: Encrypted delta export/import/rotate (three-layer tamper detection)
// - crypto-permissions.ts: Admin + ShareTarget + permission-table verify + role resolution
// - crypto-header.ts: CryptoHeader parse/clear + CEK unwrap + binary helpers + schema fingerprint
// - type-conversion.ts: MessagePack ↔ SQLite value conversion
