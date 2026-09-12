// sqlite-worker.ts
// Web Worker for executing SQL with sqlite-wasm + OPFS SAHPool VFS
// SAHPool provides synchronous OPFS access in worker context

import sqlite3InitModule from '@sqlite.org/sqlite-wasm';
import {pack, unpack} from 'msgpackr';
import {
    logger,
    registerEFCoreFunctions,
    openDatabases, pragmasSet, schemaCache,
    MODULE_NAME, bigIntUnpackr,
    setSqlite3, setPoolUtil, setBaseHref,
    bulkInsertRows, type BulkInsertHeader,
    configureCancellation, runInterruptible, isInterruptError,
} from '@sqlitewasmblazor/worker-common';
import {deltaExportEncrypted, deltaImportEncrypted, bulkRotateKey} from './crypto-delta';
import {installOpfsSAHPoolVfs as installPrfVfs} from './vfs-prf/sahpool-prf-vfs';
import {
    hasGlobalKey,
    snapshotGlobalKey,
    setGlobalKey,
    clearGlobalKey,
} from './vfs-prf/key-registry';
import {rekeySlots} from './vfs-prf/rekey';
import {clearBytes} from '@sqlitewasmblazor/crypto-core';
import {
    readPoolManifestOp,
    writePoolManifestOp,
    clearPoolManifestOp,
} from './worker-manifest';
import {
    importPoolStreamPreflight,
    importPoolStreamCommit,
} from './vfs-prf/import-streamed';
import {
    deleteStagingFile,
    openExportStaging,
    readStagingFile,
    readStagingSlice,
    sweepExportStaging,
    type ExportStagingFile,
    DECRYPT_TMP_SUFFIX,
    ENCRYPT_TMP_SUFFIX,
    MULTI_IMPORT_TMP_SUFFIX,
    planPoolSweep,
    createDatabaseImportSink,
    assertImportFileCount,
    createImportSessionHost,
    importDatabasesFromEnvelope,
    exportDatabasesToStaging,
    withHandleRecovery,
} from '@sqlitewasmblazor/worker-common';
import {
    BufferedStreamReader,
    readArrayHeader,
    readBinHeader,
    readStr,
    packArrayHeader,
    packBinHeader,
    packStr,
} from '@sqlitewasmblazor/worker-common';

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
        /** The statement was aborted with SQLITE_INTERRUPT because the request was cancelled. */
        interrupted?: boolean;
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

/**
 * Carry out what {@link planPoolSweep} decided about the entries a session
 * that died mid-flight left behind — see there for why a park goes back
 * and a temp slot goes away.
 */
function sweepUnfinishedPoolEntries(): void {
    if (!poolUtil) {
        throw new Error('sweepUnfinishedPoolEntries: pool not installed');
    }
    for (const action of planPoolSweep(poolUtil.listDatabases())) {
        if (action.kind === 'restore') {
            poolUtil.renameFile(
                `/databases/${action.park}`, `/databases/${action.database}`);
            logger.warn(
                MODULE_NAME,
                `Restored ${action.database} from ${action.park} — an import replaced ` +
                `it and never finished.`);
            continue;
        }
        poolUtil.unlink(`/databases/${action.name}`);
        logger.warn(MODULE_NAME, `Dropped unfinished write ${action.name}.`);
    }
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
            print: (...args: any[]) => logger.info(MODULE_NAME, ...args),
            printErr: (...args: any[]) => logger.error(MODULE_NAME, ...args),
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
        // (~4 slots per active WAL DB). Encrypted DBs run WAL too — the
        // offset-remap envelope covers WAL frames, so there is no reason to
        // give up crash recovery — and so carry the same slot cost. For apps
        // that open multiple DBs (TodoDb + CryptoTestDb + EncryptedTestDb +
        // PasswordTestDb + per-feature benchmarks) 10 slots is tight — we
        // default to 25 so a realistic workload doesn't trip "SAH pool is
        // full" on journal creation. 25 × ~4 KiB preallocated = ~100 KiB,
        // negligible.
        poolUtil = await installPrfVfs(sqlite3, {
            initialCapacity: 25,
            directory: '/databases',
            name: 'opfs-sahpool',
            clearOnInit: false
        });
        setPoolUtil(poolUtil);

        // Grow pool if previously created with smaller capacity (initialCapacity only applies on first creation)
        await poolUtil.reserveMinimumCapacity(25);

        // Drop export-staging leftovers from previous sessions. Staging
        // files can't be deleted at download time (the anchor download
        // drains the File lazily), so this sweep is their collection point.
        try {
            await sweepExportStaging();
        } catch (err) {
            logger.warn(MODULE_NAME, 'export-staging sweep failed:', err);
        }

        // Put back what a session that died mid-import could not.
        sweepUnfinishedPoolEntries();

        logger.info(MODULE_NAME, 'OPFS SAHPool VFS installed successfully');
        logger.debug(MODULE_NAME, 'Available VFS:', sqlite3.capi.sqlite3_vfs_find(null));

        // Signal ready to main thread
        self.postMessage({type: 'ready'});
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
/**
 * Whether SQL text and parameter values may be logged. Mirrors
 * SqliteWasmOptions.EnableCommandSqlLogging and is independent of the log
 * level: the level controls verbosity, this controls whether query content is
 * emitted at all, so tracing at Debug does not expose schema or data.
 */
let commandSqlLogging = false;

/**
 * Per-statement execution timing for benchmarking. Independent of the log
 * level so it can run in a Release build on its own, and it emits durations
 * only — never SQL text or parameters. Mirrors
 * SqliteWasmOptions.EnableRequestTracing. When off, the clock is never read.
 */
let requestTracing = false;

self.onmessage = async (event: MessageEvent<WorkerRequest | { type: 'configureLogging'; level: number; commandSql: boolean; tracing: boolean } | {
    type: 'init';
    baseHref: string;
    assetRoot?: string;
    cancelSession: string | null;
}>) => {
    // Handle initialization with base href and asset root
    if ('type' in event.data && event.data.type === 'init' && 'baseHref' in event.data) {
        baseHref = event.data.baseHref;
        setBaseHref(baseHref);
        // A null session says no service worker controls the page: the
        // progress handler is then never installed and nothing is polled.
        configureCancellation(baseHref, event.data.cancelSession);
        if (event.data.assetRoot) {
            assetRoot = event.data.assetRoot;
        }
        // Start initialization after receiving base href
        await initializeSQLite();
        return;
    }

    // Handle log level changes (no response needed)
    if ('type' in event.data && event.data.type === 'configureLogging' && 'level' in event.data) {
        logger.setLogLevel(event.data.level);
        commandSqlLogging = event.data.commandSql === true;
        requestTracing = event.data.tracing === true;
        return;
    }

    // Streaming requests — keyed by `streamId` (a negative int issued by
    // the bridge to stay clear of the C#-side positive request-id space).
    // The streaming dispatcher answers with exactly one `streamDone` /
    // `streamError` message bearing the same streamId; no `id` field.
    if ('streamId' in event.data && typeof (event.data as any).streamId === 'number') {
        const streamMsg = event.data as {
            streamId: number;
            data: { type: string };
            binaryPayload?: ArrayBuffer;
        };
        await handleStreamingRequest(
            streamMsg.streamId,
            streamMsg.data,
            streamMsg.binaryPayload,
        );
        return;
    }

    // Handle regular requests
    const {id, data, binaryPayload, binaryHeader} = event.data as WorkerRequest;

    try {
        const result = await handleRequest(id, data, binaryPayload, binaryHeader);

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
                error: error instanceof Error ? error.message : 'Unknown error',
                interrupted: isInterruptError(error),
            }
        };

        self.postMessage(response);
    }
};

/**
 * Top-level dispatcher for `streamId`-bearing messages. Each handler posts
 * exactly one `streamDone` (or `streamError`) with the same streamId, none
 * with a top-level `id` field. Export handlers write their output into an
 * OPFS staging file and report its name (plus a per-file offset table for
 * the disk export) in the streamDone message. The bridge keys its
 * StreamHandler registry by streamId; this dispatcher's only job is to
 * drop into the right handler.
 */
async function handleStreamingRequest(
    streamId: number,
    data: { type: string },
    binaryPayload?: ArrayBuffer,
): Promise<void> {
    try {
        switch (data.type) {
            case 'exportPoolToStaging':
                if (!binaryPayload) {
                    throw new Error('exportPoolToStaging requires binaryPayload (raw K_wrap)');
                }
                await exportPoolToStagingHandler(streamId, new Uint8Array(binaryPayload));
                return;
            case 'importPoolStreamPreflight':
                if (!binaryPayload) {
                    throw new Error('importPoolStreamPreflight requires binaryPayload (raw K_wrap)');
                }
                await importPoolStreamPreflightHandler(
                    streamId,
                    await importSessionHost.stagedFile(
                        requireSessionId(data), 'importPoolStreamPreflight'),
                    new Uint8Array(binaryPayload));
                return;
            case 'importPoolStreamCommit':
                if (!binaryPayload) {
                    throw new Error('importPoolStreamCommit requires binaryPayload (raw K_wrap)');
                }
                await importPoolStreamCommitHandler(
                    streamId,
                    await importSessionHost.stagedFile(
                        requireSessionId(data), 'importPoolStreamCommit'),
                    new Uint8Array(binaryPayload));
                return;
            case 'exportDatabasesToStaging':
                if (!Array.isArray((data as any).databases)) {
                    throw new Error('exportDatabasesToStaging requires data.databases (string[])');
                }
                await exportDatabasesToStagingHandler(
                    streamId, (data as any).databases as string[]);
                return;
            case 'importDatabasesFromSession':
                await importDatabasesFromSessionHandler(
                    streamId,
                    await importSessionHost.stagedFile(
                        requireSessionId(data), 'importDatabasesFromSession'),
                    (data as any).keepExisting === true);
                return;
            default:
                throw new Error(`Unknown streaming request type: ${data.type}`);
        }
    } catch (error) {
        self.postMessage({
            streamId,
            streamError: true,
            error: error instanceof Error ? error.message : String(error),
        });
    }
}

/**
 * Pass 1 of the streaming disk import — AEAD-verifies slot 0 of every
 * file in <paramref name="blob"/> under <paramref name="kWrap"/>. Returns
 * OK (0) on success or WRONG_KEY (1) on tag failure via streamDone.result.
 * Pure read; no pool mutation.
 */
async function importPoolStreamPreflightHandler(
    streamId: number,
    blob: Blob,
    kWrap: Uint8Array,
): Promise<void> {
    if (kWrap.length !== 32) {
        throw new Error(`importPoolStreamPreflight: K_wrap must be 32 bytes, got ${kWrap.length}`);
    }
    let result: number;
    try {
        result = await importPoolStreamPreflight(blob, kWrap);
    } catch (err) {
        throw err;
    } finally {
        clearBytes(kWrap);
    }
    self.postMessage({streamId, streamDone: true, result});
}

/**
 * Pass 2 of the streaming disk import — caller has wiped + EnterEncrypted,
 * so a globalKey is registered. Re-streams the envelope, decrypts each
 * slot under K_wrap, re-encrypts under globalKey via the chunked
 * writeFileSlice + atomicReplaceFile path. Returns OK (0) on streamDone.
 */
async function importPoolStreamCommitHandler(
    streamId: number,
    blob: Blob,
    kWrap: Uint8Array,
): Promise<void> {
    if (kWrap.length !== 32) {
        throw new Error(`importPoolStreamCommit: K_wrap must be 32 bytes, got ${kWrap.length}`);
    }
    if (!hasGlobalKey()) {
        throw new Error(
            'importPoolStreamCommit rejected: no globalKey registered. ' +
            'C# caller must have run EnterEncryptedAsync between preflight and commit.');
    }
    const globalKey = snapshotGlobalKey()!;
    try {
        await importPoolStreamCommit(blob, kWrap, globalKey, poolUtil!);
    } catch (err) {
        throw err;
    } finally {
        clearBytes(kWrap);
        clearBytes(globalKey);
    }
    self.postMessage({streamId, streamDone: true, result: 0});
}

/**
 * State-aware single-DB staging export, behind the plane-1
 * 'exportDbToStaging' request — the base library's
 * ExportDatabaseToDownloadAsync runs against this worker when the Crypto
 * bundle is loaded. Dispatch by hasGlobalKey():
 *   Encrypted+Unlocked → decrypt slot-by-slot to plain pages
 *   Plain pool        → copy verbatim
 *   Encrypted+Locked  → C# refuses before posting; not reachable here
 *
 * Output is always plain SQLite .db bytes — the file a downstream tool
 * (`sqlite3 file.db`) can open directly, or ImportDatabaseFromStreamAsync
 * can re-import. Every chunk is written straight into an OPFS staging file
 * via a sync access handle, so JS heap peak per op stays ~1 MB and no bytes
 * accumulate main-thread-side regardless of DB size. Returns the staging
 * file name and the plain byte count written into it.
 */
async function exportDatabaseToStaging(
    dbName: string): Promise<{ stagingFile: string; fileSize: number }> {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const dbPath = `/databases/${dbName}`;
    const fileNames = poolUtil.getFileNames();
    if (!fileNames.includes(dbPath)) {
        throw new Error(`exportDatabaseToStaging: no existing DB at ${dbPath}`);
    }

    await closeDatabase(dbName);

    const encrypted = hasGlobalKey();
    const sourceSlotSize = encrypted ? ENCRYPTED_SLOT_SIZE : PLAIN_SLOT_SIZE;
    const fileSize = poolUtil.getFileSize(dbPath);
    if (fileSize === 0 || fileSize % sourceSlotSize !== 0) {
        throw new Error(
            `exportDatabaseToStaging: ${dbName} length ${fileSize} is not a non-zero ` +
            `multiple of the expected slot size ${sourceSlotSize} ` +
            `(disk state=${encrypted ? 'encrypted' : 'plain'}).`);
    }
    const totalSlots = fileSize / sourceSlotSize;

    const globalKey = encrypted ? snapshotGlobalKey()! : undefined;
    const staging = await openExportStaging();
    try {
        for (let slotBase = 0; slotBase < totalSlots; slotBase += CHUNK_SLOTS) {
            const slotCount = Math.min(CHUNK_SLOTS, totalSlots - slotBase);
            const sourceOffset = slotBase * sourceSlotSize;
            const sourceBytes = slotCount * sourceSlotSize;

            let sourceChunk: Uint8Array | null = null;
            let plainChunk: Uint8Array | null = null;
            try {
                sourceChunk = poolUtil.exportFileSlice(dbPath, sourceOffset, sourceBytes);
                if (encrypted) {
                    // rekeySlots: source=globalKey decrypts; target=undefined
                    // emits plain pages. AAD bound to (dbPath, slotBase+i)
                    // matches what the worker wrote during EnterEncrypted /
                    // chunked plain-import / etc.
                    plainChunk = rekeySlots(
                        sourceChunk!, dbPath, globalKey, undefined, slotBase);
                } else {
                    // Plain source — sourceChunk IS the plain bytes.
                    plainChunk = sourceChunk;
                    sourceChunk = null;
                }
                staging.write(plainChunk!);
            } finally {
                if (sourceChunk !== null) {
                    clearBytes(sourceChunk);
                }
                if (plainChunk !== null) {
                    clearBytes(plainChunk);
                }
            }
        }

        // position() is the plain byte count — what a reader draining the
        // staging file will see, which is not the source's on-disk size
        // when the pool is encrypted.
        const plainSize = staging.position();
        staging.finish();
        return {stagingFile: staging.name, fileSize: plainSize};
    } catch (err) {
        await staging.abort();
        throw err;
    } finally {
        if (globalKey !== undefined) {
            clearBytes(globalKey);
        }
    }
}

/**
 * Streaming multi-DB plain export handler — writes a `.dbs` envelope into
 * an OPFS staging file.
 *
 * Wire format (MessagePack):
 *   array(N)
 *     array(2)  // file 1
 *       str(name)
 *       bin(plainBytes)
 *     array(2)  // file 2
 *       ...
 *
 * State-aware (same dispatch as the single-DB export):
 *   Plain disk        → write each file's bytes verbatim
 *   Encrypted+Unlocked → decrypt slot-by-slot to plain pages before write
 *   Encrypted+Locked  → C# caller refuses before posting (no key)
 *
 * The staging file holds the complete envelope; streamDone carries its
 * name and the bridge downloads the disk-backed File as-is.
 */
async function exportDatabasesToStagingHandler(
    streamId: number,
    dbNames: string[],
): Promise<void> {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const staging = await exportDatabasesToStaging(dbNames, {
        poolUtil,
        closeDatabase,
        crypto: hasGlobalKey()
            ? {
                snapshotKey: () => snapshotGlobalKey()!,
                toPlain: (chunk, dbPath, slotIndexBase, key) =>
                    rekeySlots(chunk, dbPath, key, undefined, slotIndexBase),
            }
            : undefined,
    });

    self.postMessage({streamId, streamDone: true, stagingFile: staging.name});
}

/**
 * Streaming multi-DB plain-import handler — consumes a `.dbs` envelope
 * from <paramref name="blob"/>. Two passes over the re-streamable Blob:
 *
 *   1. Validate: walk the whole envelope read-only — file count, tuple
 *      arity, page-aligned lengths, SQLite magic per file, no premature
 *      EOF — BEFORE any destructive pool operation. A truncated or
 *      crafted .dbs fails here with the existing disk intact.
 *   2. Commit:   wipe the pool (the destructive replace-the-disk
 *      contract — caller owns the user-facing confirmation), then
 *      per-file stream-write the plain pages through the chunked SAH
 *      path with rekey-on-write if a globalKey is registered.
 *
 * With <paramref name="keepExisting"/> the commit pass skips the wipe: C#
 * has already parked the previous content under a suffixed name and will
 * restore or drop it once it has inspected what arrived. Entries always
 * land under their real names — page AAD binds ciphertext to the database
 * path, so a file written under any other name would not decrypt there.
 *
 * State dispatch matches the single-DB import: Plain writes plain;
 * Encrypted+Unlocked rekey-on-writes; Encrypted+Locked is refused by
 * the C# caller before opening the import session.
 */
async function importDatabasesFromSessionHandler(
    streamId: number,
    blob: Blob,
    keepExisting: boolean,
): Promise<void> {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    await importDatabasesFromEnvelope(blob, keepExisting, {
        poolUtil,
        closeAllDatabases: async () => {
            for (const dbName of [...openDatabases.keys()]) {
                await closeDatabase(dbName);
            }
        },
        crypto: hasGlobalKey()
            ? {
                snapshotKey: () => snapshotGlobalKey()!,
                rekey: (chunk, dbPath, slotIndexBase, key) =>
                    rekeySlots(chunk, dbPath, undefined, key, slotIndexBase),
            }
            : undefined,
    });

    self.postMessage({streamId, streamDone: true, result: 0});
}

/**
 * Import sessions — the one way a file the user picked reaches this worker.
 *
 * C# opens a session, pushes the file one chunk at a time (awaiting each,
 * so at most one chunk is in flight), then closes it. Nothing accumulates
 * on the main thread: every chunk is a transferred ArrayBuffer that lands
 * on disk here and is wiped. That is the whole point of the design —
 * WebKit holds a Blob built from ArrayBuffers in process memory, and a
 * large import built that way is what closed the pool's access handles
 * mid-write on iOS.
 *
 * Two sinks, because two shapes of import need different things:
 *
 *   database → one plain `.db` going into one database. Single pass, so
 *              the chunks go straight into the pool's temp slot (rekeyed
 *              on the way in when the pool is encrypted) and no copy of
 *              the file exists anywhere.
 *   staging  → a `.dbs` or `.eds` envelope, which is validated in one
 *              pass and committed in another. It lands in an OPFS staging
 *              file the worker re-streams per pass.
 */
/**
 * The chunk pump C# pushes a picked file through. The session machinery is
 * plane-neutral and lives in worker-common; what plane 2 supplies is the
 * opener below — the snapshot of the global key and the rekey transform that
 * turns each batch into encrypted slots.
 *
 * The key is snapshotted at open because the session outlives the call that
 * started it and a lock can clear the registry underneath it; the snapshot is
 * wiped when the session ends, whichever way it ends.
 */
const importSessionHost = createImportSessionHost({
    async openDatabaseSink(dbName, plainSize) {
        if (!sqlite3 || !poolUtil) {
            throw new Error('SQLite not initialized');
        }
        await closeDatabase(dbName);
        let globalKey = hasGlobalKey() ? snapshotGlobalKey()! : undefined;
        try {
            return {
                sink: createDatabaseImportSink(
                    dbName, plainSize, poolUtil, globalKey,
                    globalKey === undefined
                        ? undefined
                        : (chunk, dbPath, slotIndexBase, key) =>
                            rekeySlots(chunk, dbPath, undefined, key, slotIndexBase)),
                dispose() {
                    if (globalKey !== undefined) {
                        clearBytes(globalKey);
                        globalKey = undefined;
                    }
                },
            };
        } catch (err) {
            if (globalKey !== undefined) {
                clearBytes(globalKey);
            }
            throw err;
        }
    },
    onDatabaseCommitted(dbName) {
        logger.info(MODULE_NAME, `✓ Imported ${dbName}`);
    },
});

// The worker protocol answers every request with a result object; the session
// host returns nothing, so these adapt rather than reimplement.
async function openImportSession(
    sessionId: number,
    sink: string,
    dbName: string | undefined,
    size: number | undefined,
) {
    await importSessionHost.open(sessionId, sink, dbName, size);
    return {rowsAffected: 0};
}

function appendToImportSession(sessionId: number, chunk: Uint8Array) {
    importSessionHost.append(sessionId, chunk);
    return {rowsAffected: 0};
}

function closeImportSession(sessionId: number) {
    importSessionHost.close(sessionId);
    return {rowsAffected: 0};
}

async function discardImportSession(sessionId: number) {
    await importSessionHost.discard(sessionId);
    return {rowsAffected: 0};
}

/** The session id a streaming import request must carry. */
function requireSessionId(data: { type: string }): number {
    const sessionId = (data as any).sessionId;
    if (typeof sessionId !== 'number') {
        throw new Error(`${data.type} requires data.sessionId`);
    }
    return sessionId;
}

/**
 * Chunked encrypted-disk export — worker side. Loops every DB in the SAH
 * pool and writes its rekeyed slot batches (under the envelope's
 * per-export K_wrap) contiguously into an OPFS staging file. JS heap peak
 * per chunk is one slot batch (~1 MB) regardless of total disk size.
 *
 * streamDone carries the staging file name plus a per-file
 * { name, offset, size } table; the bridge composes the v3 envelope from
 * its MessagePack header bytes and disk-backed File.slice() segments, so
 * the ciphertext never occupies main-thread memory.
 *
 * Precondition (enforced by caller): the worker is Encrypted+Unlocked —
 * a globalKey is registered and every DB is slot-format ciphertext under
 * it. We read each DB chunk via exportFileSlice (so the worker reads at
 * most one chunk into JS heap at a time) and decrypt+re-encrypt under
 * K_wrap via the chunked rekeySlots path.
 */
async function exportPoolToStagingHandler(streamId: number, kWrap: Uint8Array): Promise<void> {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    if (kWrap.length !== 32) {
        throw new Error(`exportPoolToStaging: K_wrap must be 32 bytes, got ${kWrap.length}`);
    }
    if (!hasGlobalKey()) {
        throw new Error(
            'exportPoolToStaging rejected: no globalKey registered. Caller must ' +
            'have Unlocked the disk before invoking the streaming export.');
    }

    const globalKey = snapshotGlobalKey()!;
    const staging = await openExportStaging();
    const files: { name: string; offset: number; size: number }[] = [];
    try {
        const names = poolUtil.listDatabases();

        for (const name of names) {
            await closeDatabase(name);
            const dbPath = `/databases/${name}`;
            const fileSize = poolUtil.getFileSize(dbPath);
            if (fileSize === 0 || fileSize % ENCRYPTED_SLOT_SIZE !== 0) {
                throw new Error(
                    `exportPoolToStaging: ${name} length ${fileSize} is not a non-zero ` +
                    `multiple of the encrypted slot size ${ENCRYPTED_SLOT_SIZE}; ` +
                    `refusing to export a non-encrypted source.`);
            }
            const totalSlots = fileSize / ENCRYPTED_SLOT_SIZE;
            const fileOffset = staging.position();

            for (let slotBase = 0; slotBase < totalSlots; slotBase += CHUNK_SLOTS) {
                const slotCount = Math.min(CHUNK_SLOTS, totalSlots - slotBase);
                const encryptedOffset = slotBase * ENCRYPTED_SLOT_SIZE;
                const encryptedBytes = slotCount * ENCRYPTED_SLOT_SIZE;

                let sourceChunk: Uint8Array | null = null;
                let rekeyedChunk: Uint8Array | null = null;
                try {
                    sourceChunk = poolUtil.exportFileSlice(dbPath, encryptedOffset, encryptedBytes);
                    // K_old → K_wrap re-encrypt. Output is the same slot
                    // format (4124 bytes per slot, AAD-bound to dbPath +
                    // global slot index).
                    rekeyedChunk = rekeySlots(sourceChunk!, dbPath, globalKey, kWrap, slotBase);
                    staging.write(rekeyedChunk);
                } finally {
                    if (sourceChunk !== null) {
                        clearBytes(sourceChunk);
                    }
                    if (rekeyedChunk !== null) {
                        clearBytes(rekeyedChunk);
                    }
                }
            }
            files.push({name, offset: fileOffset, size: staging.position() - fileOffset});
        }

        staging.finish();
        self.postMessage({streamId, streamDone: true, stagingFile: staging.name, files});
    } catch (err) {
        await staging.abort();
        throw err;
    } finally {
        // globalKey snapshot — clear so K doesn't linger past export.
        clearBytes(globalKey);
    }
}

async function handleRequest(requestId: number, data: WorkerRequest['data'], binaryPayload?: ArrayBuffer, binaryHeader?: ArrayBuffer) {
    const {type, database, sql, parameters} = data;

    switch (type) {
        case 'open':
            // Single-key model: the worker uses globalKey set via
            // setGlobalEncryptionKey (see SetEncryptionKeyAsync on the C#
            // side). Open carries no key envelope.
            return await openDatabase(database!);

        case 'exportDbToStaging':
            // Plane-1-compatible request shape (base library's
            // ExportDatabaseToDownloadAsync). State-aware: Encrypted+
            // Unlocked decrypts to plain pages, Plain copies verbatim.
            return await exportDatabaseToStaging(database!);

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
            return {databases: poolUtil.listDatabases()};

        case 'execute':
            // When binaryPayload is present, blob params carry
            // { __blobOffset, __blobLength } placeholders pointing into the
            // attached buffer instead of Base64 strings in the JSON.
            // convertParametersForBinding reads bytes from binaryPayload.
            return await executeSql(
                requestId, database!, sql!, parameters || {},
                binaryPayload ? new Uint8Array(binaryPayload) : undefined);

        case 'close':
            return await closeDatabase(database!);

        case 'exists':
            return await checkDatabaseExists(database!);

        case 'delete':
            return await deleteDatabase(database!);

        case 'rename':
            return await renameDatabase(database!, (data as any).newName);

        case 'replaceDb':
            return await replaceDatabase(database!, (data as any).targetName);

        case 'importSessionOpen':
            return await openImportSession(
                (data as any).sessionId,
                (data as any).sink,
                database,
                (data as any).size);

        case 'importSessionAppend':
            if (!binaryPayload) {
                throw new Error('importSessionAppend requires binaryPayload');
            }
            return appendToImportSession(
                (data as any).sessionId, new Uint8Array(binaryPayload));

        case 'importSessionClose':
            return closeImportSession((data as any).sessionId);

        case 'importSessionDiscard':
            return await discardImportSession((data as any).sessionId);

        case 'importDb':
            if (!binaryPayload) {
                throw new Error('importDb requires binaryPayload');
            }
            return await importDatabase(
                database!,
                new Uint8Array(binaryPayload),
                (data as any).opaque === true
            );

        case 'exportDb': {
            // Plane-1 contract (ISqliteWasmDatabaseService.ExportDatabaseAsync):
            // VERBATIM export — raw OPFS bytes, slot-format ciphertext or
            // plain pages, whatever is on disk. The byte[]-shuttle rekey/
            // encrypt/plain modes were deleted with the streaming refactor;
            // only verbatim survives because the base plane's public API
            // still sends it.
            const mode = (data as any).mode as string;
            if (mode !== 'verbatim') {
                throw new Error(
                    `exportDb mode='${mode}' is no longer supported; ` +
                    `use the streaming export/import surface instead.`);
            }
            return await exportDatabaseVerbatim(database!);
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

        case 'readStagingSlice':
            return {
                rawBinary: true,
                data: await readStagingSlice(
                    (data as any).name, (data as any).offset, (data as any).length),
            };

        case 'deleteStagingFile':
            await deleteStagingFile((data as any).name);
            return {success: true};

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

        case 'readPoolManifest':
            // Disk-bound passkey manifest read. Walks every DB in the SAHPool,
            // pulls bytes 524..1023 of each header sector, asserts they all
            // match, and parses out the body. When `verifyMac` is true (only
            // valid post-unlock — globalKey present) the HMAC is also checked.
            return await readPoolManifestOp((data as any).verifyMac === true);

        case 'writePoolManifest':
            // Disk-bound passkey manifest write. binaryPayload carries the
            // (already-MessagePack-serialized) body bytes from C#; globalKey
            // MUST be installed so we can HKDF-derive the manifest MAC key.
            // Writes the same 500-byte region into every DB's header sector.
            if (!binaryPayload) {
                throw new Error('writePoolManifest requires binaryPayload (manifest body bytes)');
            }
            return await writePoolManifestOp(new Uint8Array(binaryPayload));

        case 'clearPoolManifest':
            // Zero bytes 524..1023 of every DB's header sector. Used by
            // LeaveEncryptedAsync / ResetPoolAsync when the disk transitions
            // out of Encrypted state. No globalKey requirement (we're erasing,
            // not authenticating).
            return await clearPoolManifestOp();

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

    return {success: true};
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
    return {success: true};
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
    return {success: true};
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

// Scope helper: unpack a VfsKeyHeader, hand the 32-byte key to `fn`, wipe
// on exit. Use for transient-use sites (encryptDb). Do NOT use when the
// key's ownership transfers to a longer-lived owner (setGlobalKey takes
// ownership and wipes on its own swap).
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
            const {value, type} = paramData;

            if (value === null || value === undefined) {
                converted[key] = null;
                if (commandSqlLogging) {
                    logger.debug(MODULE_NAME, `[PARAM] ${key}: null`);
                }
            } else if (type === 'blob' && binaryPayload && value && typeof value === 'object'
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
                if (commandSqlLogging) {
                    logger.debug(MODULE_NAME, `[PARAM] ${key}: blob (${length} bytes from binary attachment @ ${offset})`);
                }
            } else if (type === 'blob' && typeof value === 'string') {
                // Legacy fallback — Base64-encoded blob in the JSON message.
                try {
                    const binaryString = atob(value);
                    const bytes = new Uint8Array(binaryString.length);
                    for (let i = 0; i < binaryString.length; i++) {
                        bytes[i] = binaryString.charCodeAt(i);
                    }
                    converted[key] = bytes;
                    if (commandSqlLogging) {
                        logger.debug(MODULE_NAME, `[PARAM] ${key}: blob (${bytes.length} bytes from base64)`);
                    }
                } catch (e) {
                    logger.error(MODULE_NAME, `[PARAM] Failed to decode blob ${key}:`, e);
                    converted[key] = value;
                }
            } else {
                // For text, integer, real - use value as-is
                converted[key] = value;
                if (commandSqlLogging) {
                    logger.debug(MODULE_NAME, `[PARAM] ${key}: ${type} = ${typeof value === 'string' && value.length > 50 ? value.substring(0, 50) + '...' : value}`);
                }
            }
        } else {
            // Fallback for old format (backwards compatibility)
            logger.warn(MODULE_NAME, `[PARAM] ${key}: using legacy format (no type metadata)`);
            converted[key] = paramData;
        }
    }

    return converted;
}

async function executeSql(
    requestId: number,
    dbName: string, sql: string,
    parameters: Record<string, any>,
    binaryPayload?: Uint8Array,
) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    let execStartedAt = 0;
    try {
        if (commandSqlLogging) {
            logger.debug(MODULE_NAME, 'Executing SQL:', sql.substring(0, 100));
        }

        // Convert parameters with type metadata for proper SQLite binding.
        // binaryPayload (if present) carries blob param bytes — see
        // convertParametersForBinding for the __blobOffset/__blobLength
        // placeholder shape.
        const convertedParams = convertParametersForBinding(parameters, binaryPayload);

        // Execute SQL - use returnValue to get the result.
        // Timed on its own: the bridge measures send -> response, which is this
        // plus however long the request waited for the worker to be free. The
        // difference between the two numbers is the queue wait.
        // Statement shape is read from the statement *before* it runs, never by
        // re-preparing it afterwards. Two reasons:
        //   - a query that matched nothing still has columns, and without them
        //     EF's FromSql/SqlQuery fails with "The required column 'X' was not
        //     present in the results of a 'FromSql' operation";
        //   - re-preparing after execution is unsound for anything that alters
        //     the schema — preparing `ALTER TABLE ... ADD COLUMN` a second time
        //     fails with "duplicate column name", and DROP COLUMN likewise.
        // Declared types come from the schema here; columns the schema cannot
        // explain are inferred from the first row after the statement has run.
        const columnNames: string[] = [];
        const columnTypes: string[] = [];
        const inferFromValue: number[] = [];

        {
            const shape = db.prepare(sql);
            try {
                const colCount = shape.columnCount;

                let tableSchema: Map<string, string> | null = null;
                if (colCount > 0 && sql.trim().toUpperCase().startsWith('SELECT')) {
                    const tableName = extractTableName(sql);
                    if (tableName) {
                        tableSchema = getTableSchema(db, dbName, tableName);
                    }
                }

                for (let i = 0; i < colCount; i++) {
                    const colName = shape.getColumnName(i);
                    columnNames.push(colName);

                    const declaredType = tableSchema?.get(colName);
                    if (declaredType) {
                        const typeUpper = declaredType.toUpperCase();
                        if (typeUpper.includes('INT')) {
                            columnTypes.push('INTEGER');
                        } else if (typeUpper.includes('REAL') || typeUpper.includes('DOUBLE') || typeUpper.includes('FLOAT')) {
                            columnTypes.push('REAL');
                        } else if (typeUpper.includes('BLOB')) {
                            columnTypes.push('BLOB');
                        } else {
                            columnTypes.push('TEXT');
                        }
                    } else {
                        columnTypes.push('TEXT');
                        inferFromValue.push(i);
                    }
                }
            } finally {
                shape.finalize();
            }
        }

        // Interruptible: while this runs, a cancel from the page reaches the
        // progress handler through the service worker and aborts the
        // statement with SQLITE_INTERRUPT. See cancel-poll.ts.
        execStartedAt = requestTracing ? performance.now() : 0;
        const result = runInterruptible(db, requestId, () => db.exec({
            sql: sql,
            bind: Object.keys(convertedParams).length > 0 ? convertedParams : undefined,
            returnValue: 'resultRows',
            rowMode: 'array'
        }));

        if (requestTracing) {
            console.log(
                `[SQLite Worker] SQL executed in ${(performance.now() - execStartedAt).toFixed(0)} ms,`,
                `rows: ${result?.length || 0}`);
        }

        // Columns the schema could not explain: infer from the first row, if any.
        if (inferFromValue.length > 0 && result && result.length > 0) {
            for (const i of inferFromValue) {
                const value = result[0][i];
                if (value === null || value === undefined) {
                    continue;
                }
                if (typeof value === 'number') {
                    columnTypes[i] = Number.isInteger(value) ? 'INTEGER' : 'REAL';
                } else if (typeof value === 'bigint') {
                    columnTypes[i] = 'INTEGER';
                } else if (typeof value === 'boolean') {
                    columnTypes[i] = 'INTEGER';
                } else if (value instanceof Uint8Array || ArrayBuffer.isView(value)) {
                    columnTypes[i] = 'BLOB';
                }
            }
        }

        // Get changes and last insert ID for non-SELECT queries
        let rowsAffected = 0;
        let lastInsertId = 0;

        const trimmedUpper = sql.trim().toUpperCase();
        if (trimmedUpper.startsWith('INSERT') ||
            trimmedUpper.startsWith('UPDATE') ||
            trimmedUpper.startsWith('DELETE') ||
            trimmedUpper.startsWith('CREATE')) {

            // Check if statement has RETURNING clause
            // When RETURNING is used, db.changes() doesn't work correctly because
            // SQLite treats it as a SELECT-like operation
            const hasReturning = sql.toUpperCase().includes('RETURNING');

            if (hasReturning && result && result.length > 0) {
                // For UPDATE/DELETE with RETURNING, the presence of a result row means success
                rowsAffected = result.length;
            } else {
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
        if (isInterruptError(error)) {
            // The caller asked for this; it is not a failure of the statement.
            if (requestTracing) {
                console.log(`[SQLite Worker] SQL interrupted after ${(performance.now() - execStartedAt).toFixed(0)} ms`);
            }
            throw error;
        }
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
    return {success: true};
}

/**
 * True for the DOMException a sync access handle throws once the platform
 * has closed it ("AccessHandle is closed"). WebKit reclaims the storage
 * layer under memory pressure while the page lives on, and from that
 * moment every pool call fails the same way — including the rename that
 * would put a parked database back.
 */
async function checkDatabaseExists(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        // Check if database is currently open
        if (openDatabases.has(dbName)) {
            return {rowsAffected: 1};  // exists
        }

        // Check if database file exists in OPFS SAHPool
        const dbPath = `/databases/${dbName}`;

        // Try to check file existence using poolUtil's file list
        // The poolUtil exposes information about stored databases
        if (poolUtil.getFileNames) {
            const files = await poolUtil.getFileNames();
            const exists = files.includes(dbPath);
            return {rowsAffected: exists ? 1 : 0};
        }

        // Fallback: try to open database to check if it exists
        try {
            const testDb = new poolUtil.OpfsSAHPoolDb(dbPath);
            testDb.close();
            return {rowsAffected: 1};  // exists
        } catch {
            return {rowsAffected: 0};  // doesn't exist
        }
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to check database ${dbName}:`, error);
        // On error, assume it doesn't exist
        return {rowsAffected: 0};
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
            const deleted = await withHandleRecovery(
                `delete ${dbName}`, () => poolUtil!.unlink(dbPath));
            if (deleted) {
                logger.info(MODULE_NAME, `✓ Deleted database: ${dbName}`);
            } else {
                logger.debug(MODULE_NAME, `Database ${dbName} was not in OPFS (already deleted or never created)`);
            }
        } else {
            logger.warn(MODULE_NAME, `unlink not available, database may persist`);
        }

        return {success: true};
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to delete database ${dbName}:`, error);
        throw error;
    }
}

/**
 * Verbatim byte[] export — plane-1 contract behind
 * ISqliteWasmDatabaseService.ExportDatabaseAsync. Returns raw OPFS bytes:
 * slot-format ciphertext on an encrypted disk, plain pages on a plain one
 * (the importer auto-detects by SQLite magic). Closes the DB first so the
 * snapshot is consistent — the C# side mirrors that by dropping the name
 * from its open-databases set.
 */
async function exportDatabaseVerbatim(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    const dbPath = `/databases/${dbName}`;
    await closeDatabase(dbName);
    const raw: Uint8Array = poolUtil.exportFile(dbPath);
    logger.info(MODULE_NAME, `✓ Exported verbatim ${dbName}: ${raw.length}B`);
    return {rawBinary: true, data: raw};
}

/**
 * Put <paramref name="sourceName"/> in <paramref name="targetName"/>'s
 * place: the target's slot is freed and the source's slot re-tagged with
 * the target's path, in one pool metadata update. No bytes are copied.
 *
 * This is what park/restore needs and a plain rename cannot give it. A
 * rename onto an occupied name silently drops the occupant's slot out of
 * the path map — it stays claimed and its bytes stay unreachable — and
 * splitting it into delete-then-rename opens a window where a failure
 * between the two leaves both the park and what it was meant to replace
 * standing, with nothing left to say which one is the database.
 */
async function replaceDatabase(sourceName: string, targetName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    // Both slots change identity here; an OFile that captured either SAH at
    // xOpen keeps serving pages from a slot that now belongs to the other
    // path. C# reopens on demand.
    await closeDatabase(sourceName);
    await closeDatabase(targetName);
    await withHandleRecovery(
        `replace ${targetName} with ${sourceName}`,
        () => poolUtil!.atomicReplaceFile(
            `/databases/${sourceName}`, `/databases/${targetName}`));
    logger.info(MODULE_NAME, `✓ Replaced ${targetName} with ${sourceName}`);
    return {success: true};
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
            await withHandleRecovery(
                `rename ${oldName} → ${newName}`,
                () => poolUtil!.renameFile(oldPath, newPath));
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

        return {success: true};
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
                return {rowsAffected: 2};
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
                return {rowsAffected: 1};
            }
            logger.debug(
                MODULE_NAME,
                `Verify-on-write OK for ${dbName} (slot 0: ${verify})`,
            );
        }

        logger.info(MODULE_NAME, `✓ Imported database: ${dbName} (${data.length} bytes)`);

        // VfsImportResult.OK = 0
        return {rowsAffected: 0};
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
 * Number of plain SQLite pages processed per chunk iteration. 256 pages
 * × 4096 B = 1 MB plain input, which expands to ~1.03 MB encrypted output
 * (256 × 4124 B). Both stay well under the per-op JS heap budget on iPad
 * Safari (~150 MB renderer cap shared with WASM heap).
 */
const CHUNK_SLOTS = 256;

async function encryptDatabaseInPlace(dbName: string, key: Uint8Array) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    if (key.length !== 32) {
        throw new Error(`encryptDb: key must be exactly 32 bytes, got ${key.length}`);
    }

    const dbPath = `/databases/${dbName}`;
    const tempPath = `${dbPath}${ENCRYPT_TMP_SUFFIX}`;

    // Install-K-first ordering (D.1): a globalKey is already registered
    // before this loop runs — the caller (EnterEncryptedAsync) installed
    // K precisely so a rollback decrypt can run under the same key on
    // mid-loop failure. The shape check below (% PLAIN_SLOT_SIZE != 0)
    // + the SQLite magic-header probe make it structurally impossible
    // for an already-encrypted file to slip through this path: a
    // ciphertext file's length divides by ENCRYPTED_SLOT_SIZE (4124),
    // not PLAIN_SLOT_SIZE (4096), and its first 16 bytes are AEAD
    // ciphertext, not the "SQLite format 3\0" magic.

    const fileNames: string[] = poolUtil.getFileNames();
    if (!fileNames.includes(dbPath)) {
        throw new Error(`encryptDb: no existing DB at ${dbPath}`);
    }

    await closeDatabase(dbName);

    // Shape check on the full file size before reading any chunk. A real
    // encrypted-at-rest file (4124-byte slots) divides differently from
    // plain pages (4096-byte slots); refuse to treat the wrong shape as
    // plain pages and corrupt it.
    const fileSize = poolUtil.getFileSize(dbPath);
    if (fileSize === 0 || fileSize % PLAIN_SLOT_SIZE !== 0) {
        throw new Error(
            `encryptDb: ${dbName} length ${fileSize} is not a non-zero multiple of ` +
            `the plain page size ${PLAIN_SLOT_SIZE}; refusing to encrypt a non-plain source.`,
        );
    }
    const totalSlots = fileSize / PLAIN_SLOT_SIZE;

    // SQLite magic header probe on the first 16 bytes — guards against a
    // file whose length matches plain pages by coincidence. The probe
    // reads only what it needs (no full-file read).
    const headerProbe = poolUtil.exportFileSlice(dbPath, 0, SQLITE_MAGIC_HEADER.length);
    try {
        if (!hasSqliteMagicHeader(headerProbe)) {
            throw new Error(
                `encryptDb: ${dbName} does not start with the SQLite magic header — ` +
                `refusing to treat ciphertext as plain pages.`,
            );
        }
    } finally {
        clearBytes(headerProbe);
    }

    // Clean up any leftover temp slot from a prior crashed attempt before
    // we start writing. unlink is no-op on missing paths.
    if (poolUtil.getFileNames().includes(tempPath)) {
        try {
            poolUtil.unlink(tempPath);
        } catch { /* best-effort */
        }
    }

    try {
        for (let slotBase = 0; slotBase < totalSlots; slotBase += CHUNK_SLOTS) {
            const slotCount = Math.min(CHUNK_SLOTS, totalSlots - slotBase);
            const plainOffset = slotBase * PLAIN_SLOT_SIZE;
            const plainBytes = slotCount * PLAIN_SLOT_SIZE;
            const encryptedOffset = slotBase * ENCRYPTED_SLOT_SIZE;

            let plainChunk: Uint8Array | null = null;
            let encryptedChunk: Uint8Array | null = null;
            try {
                plainChunk = poolUtil.exportFileSlice(dbPath, plainOffset, plainBytes);
                encryptedChunk = rekeySlots(plainChunk!, dbPath, undefined, key, slotBase);
                poolUtil.writeFileSlice(tempPath, encryptedOffset, encryptedChunk!);
            } finally {
                // plainChunk is sensitive plaintext — wipe.
                if (plainChunk !== null) {
                    clearBytes(plainChunk);
                }
                if (encryptedChunk !== null) {
                    clearBytes(encryptedChunk);
                }
            }
        }

        // Promote temp → real. The src SAH slot becomes the new live
        // DB slot in one metadata-only rename; the old plain slot is
        // freed back to the pool.
        poolUtil.atomicReplaceFile(tempPath, dbPath);

        logger.info(
            MODULE_NAME,
            `✓ Encrypted in place ${dbName}: ${fileSize}B (${totalSlots} slots) chunked`,
        );

        return {rowsAffected: 0};
    } catch (err) {
        // Mid-loop or post-rename failure: drop the temp slot if it still
        // has data. The real path is untouched until atomicReplaceFile.
        try {
            poolUtil.unlink(tempPath);
        } catch { /* best-effort */
        }
        throw err;
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
    const tempPath = `${dbPath}${DECRYPT_TMP_SUFFIX}`;

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

    try {
        await closeDatabase(dbName);

        const fileSize = poolUtil.getFileSize(dbPath);
        if (fileSize === 0 || fileSize % ENCRYPTED_SLOT_SIZE !== 0) {
            throw new Error(
                `decryptDb: ${dbName} length ${fileSize} is not a non-zero multiple of ` +
                `the encrypted slot size ${ENCRYPTED_SLOT_SIZE}; registry says encrypted but ` +
                `the file shape says plain — refusing to decrypt a non-encrypted source.`,
            );
        }
        const totalSlots = fileSize / ENCRYPTED_SLOT_SIZE;

        if (poolUtil.getFileNames().includes(tempPath)) {
            try {
                poolUtil.unlink(tempPath);
            } catch { /* best-effort */
            }
        }

        for (let slotBase = 0; slotBase < totalSlots; slotBase += CHUNK_SLOTS) {
            const slotCount = Math.min(CHUNK_SLOTS, totalSlots - slotBase);
            const encryptedOffset = slotBase * ENCRYPTED_SLOT_SIZE;
            const encryptedBytes = slotCount * ENCRYPTED_SLOT_SIZE;
            const plainOffset = slotBase * PLAIN_SLOT_SIZE;

            let encryptedChunk: Uint8Array | null = null;
            let plainChunk: Uint8Array | null = null;
            try {
                encryptedChunk = poolUtil.exportFileSlice(dbPath, encryptedOffset, encryptedBytes);
                plainChunk = rekeySlots(encryptedChunk!, dbPath, sourceKey, undefined, slotBase);
                poolUtil.writeFileSlice(tempPath, plainOffset, plainChunk!);
            } finally {
                if (encryptedChunk !== null) {
                    clearBytes(encryptedChunk);
                }
                // plainChunk is sensitive plaintext — wipe.
                if (plainChunk !== null) {
                    clearBytes(plainChunk);
                }
            }
        }

        poolUtil.atomicReplaceFile(tempPath, dbPath);

        logger.info(
            MODULE_NAME,
            `✓ Decrypted in place ${dbName}: ${fileSize}B (${totalSlots} slots) chunked`,
        );

        return {rowsAffected: 0};
    } catch (err) {
        try {
            poolUtil.unlink(tempPath);
        } catch { /* best-effort */
        }
        throw err;
    } finally {
        // K_old (snapshot) — wipe so it doesn't linger past the op.
        // globalKey lifecycle remains caller-controlled.
        clearBytes(sourceKey);
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
