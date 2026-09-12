// sqlite-worker.ts — plane 1 (plain SQLite engine, no crypto)
// Web Worker for executing SQL with sqlite-wasm + vendor OPFS SAHPool VFS.
//
// For PRF-keyed at-rest encryption (and the worker-side crypto handlers it
// needs: setGlobalEncryptionKey, encryptDb, deltaExport, etc.), consumers
// reference SqliteWasmBlazor.Crypto + SqliteWasmBlazor.Crypto.UI — those
// ship a superset worker bundle from `_content/SqliteWasmBlazor.Crypto/`
// that AddSqliteWasmBlazorCrypto()'s DI extension points the bridge at.

import sqlite3InitModule from '@sqlite.org/sqlite-wasm';
import { pack, unpack } from 'msgpackr';
import {
    logger,
    registerEFCoreFunctions,
    openDatabases, pragmasSet, schemaCache,
    MODULE_NAME, bigIntUnpackr,
    setSqlite3, setPoolUtil, setBaseHref,
    bulkInsertRows, type BulkInsertHeader,
    configureCancellation, runInterruptible, isInterruptError,
    openExportStaging, sweepExportStaging,
    deleteStagingFile, readStagingSlice,
    createDatabaseImportSink,
    createImportSessionHost,
    exportDatabasesToStaging,
    importDatabasesFromEnvelope,
    planPoolSweep,
    withHandleRecovery,
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
 * Settle pool entries an import left behind. A park whose database is gone
 * is that database — the restore that would have put it back never ran — so
 * it goes back under its own name; a temp slot no promotion ever reached is
 * dropped. See planPoolSweep for the decision table.
 */
function sweepUnfinishedPoolEntries() {
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

        // Install vendor OPFS SAHPool VFS (plane 1 — no encryption).
        // Plane-2's worker bundle (SqliteWasmBlazor.Crypto) ships a forked
        // installer that adds per-page ChaCha20-Poly1305 encryption when a
        // global key is registered; this plane stays page-for-page identical
        // to the vendor implementation.
        //
        // Pool capacity: each DB occupies 1 slot for the main file; in
        // journal_mode=WAL it may also claim `.db-wal` and `.db-shm` slots
        // plus a transient `.db-journal` during the WAL mode transition
        // (~4 slots per active WAL DB). For apps that open multiple DBs
        // (Todo + Notes + per-feature benchmarks) 10 slots is tight — we
        // default to 25 so a realistic workload doesn't trip "SAH pool is
        // full" on journal creation. 25 × ~4 KiB preallocated = ~100 KiB.
        poolUtil = await (sqlite3 as any).installOpfsSAHPoolVfs({
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

self.onmessage = async (event: MessageEvent<WorkerRequest | { type: 'configureLogging'; level: number; commandSql: boolean; tracing: boolean } | { type: 'init'; baseHref: string; assetRoot?: string; cancelSession: string | null }>) => {
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
        const streamMsg = event.data as unknown as {
            streamId: number;
            data: { type: string };
        };
        await handleStreamingRequest(streamMsg.streamId, streamMsg.data);
        return;
    }

    // Handle regular requests
    const { id, data, binaryPayload, binaryHeader } = event.data as WorkerRequest;

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
 * exactly one `streamDone` (or `streamError`) under the same streamId and
 * none with a top-level `id`. The export handler writes its output into an
 * OPFS staging file and reports its name; the import handler reads the file
 * its session already staged. Neither moves bytes through postMessage.
 */
async function handleStreamingRequest(
    streamId: number,
    data: { type: string },
): Promise<void> {
    try {
        switch (data.type) {
            case 'exportDatabasesToStaging': {
                if (!Array.isArray((data as any).databases)) {
                    throw new Error('exportDatabasesToStaging requires data.databases (string[])');
                }
                const staging = await exportDatabasesToStagingOp(
                    (data as any).databases as string[]);
                self.postMessage({streamId, streamDone: true, stagingFile: staging});
                return;
            }
            case 'importDatabasesFromSession': {
                const sessionId = (data as any).sessionId;
                if (typeof sessionId !== 'number') {
                    throw new Error('importDatabasesFromSession requires data.sessionId');
                }
                await importDatabasesFromSessionOp(
                    await importSessionHost.stagedFile(sessionId, data.type),
                    (data as any).keepExisting === true);
                self.postMessage({streamId, streamDone: true, result: 0});
                return;
            }
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

async function handleRequest(requestId: number, data: WorkerRequest['data'], binaryPayload?: ArrayBuffer, binaryHeader?: ArrayBuffer) {
    const { type, database, sql, parameters } = data;

    switch (type) {
        case 'open':
            // Single-key model: the worker uses globalKey set via
            // setGlobalEncryptionKey (see SetEncryptionKeyAsync on the C#
            // side). Open carries no key envelope.
            return await openDatabase(database!);

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
            await importSessionHost.open(
                (data as any).sessionId, (data as any).sink,
                database, (data as any).size);
            return { rowsAffected: 0 };

        case 'importSessionAppend':
            if (!binaryPayload) {
                throw new Error('importSessionAppend requires binaryPayload');
            }
            importSessionHost.append(
                (data as any).sessionId, new Uint8Array(binaryPayload));
            return { rowsAffected: 0 };

        case 'importSessionClose':
            importSessionHost.close((data as any).sessionId);
            return { rowsAffected: 0 };

        case 'importSessionDiscard':
            await importSessionHost.discard((data as any).sessionId);
            return { rowsAffected: 0 };

          case 'importDb':
              if (!binaryPayload) {
                  throw new Error('importDb requires binaryPayload');
              }
              // Opaque input is ciphertext of a PRF-VFS database, which this
              // plane has no key for and no concept of. Refuse it by name
              // rather than handing non-SQLite bytes to the vendor pool and
              // surfacing its header-check message.
              if ((data as any).opaque === true) {
                  throw new Error(
                      'importDb opaque=true requires SqliteWasmBlazor.Crypto. ' +
                      'Plane-1 worker imports plain SQLite files only.');
              }
              return await importDatabase(database!, new Uint8Array(binaryPayload));

          case 'exportDb': {
            // Plane 1 only handles VERBATIM export — raw OPFS bytes
            // (page-level mirror of whatever's on disk, plain or opaque).
            // Plane 2 (SqliteWasmBlazor.Crypto) ships the rekey/encrypt
            // modes that require a VfsKeyHeader payload.
            const mode = (data as any).mode as 'verbatim' | 'plain' | 'rekey' | 'encrypt';
            if (mode !== 'verbatim' && mode !== 'plain') {
                throw new Error(
                    `exportDb mode='${mode}' requires SqliteWasmBlazor.Crypto. ` +
                    `Plane-1 worker only handles 'verbatim' and 'plain'.`);
            }
            return await exportDatabase(database!, mode);
        }

        case 'exportDbToStaging':
            return await exportDatabaseToStaging(database!);

        case 'readStagingSlice':
            return {
                rawBinary: true,
                data: await readStagingSlice(
                    (data as any).name, (data as any).offset, (data as any).length),
            };

        case 'deleteStagingFile':
            await deleteStagingFile((data as any).name);
            return { success: true };

        case 'importRows':
            if (!binaryPayload) {
                throw new Error('importRows requires binaryPayload (MessagePack)');
            }
            return importRows(database!, new Uint8Array(binaryPayload), data as any);

        default:
            throw new Error(
                `Unknown request type: ${type}. ` +
                `Encrypted-VFS / delta-sync ops require SqliteWasmBlazor.Crypto's worker bundle.`);
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
                if (commandSqlLogging) {
                    logger.debug(MODULE_NAME, `[PARAM] ${key}: null`);
                }
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
                if (commandSqlLogging) {
                    logger.debug(MODULE_NAME, `[PARAM] ${key}: blob (${length} bytes from binary attachment @ ${offset})`);
                }
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
                    if (commandSqlLogging) {
                        logger.debug(MODULE_NAME, `[PARAM] ${key}: blob (${bytes.length} bytes from base64)`);
                    }
                } catch (e) {
                    logger.error(MODULE_NAME, `[PARAM] Failed to decode blob ${key}:`, e);
                    converted[key] = value;
                }
            }
            else {
                // For text, integer, real - use value as-is
                converted[key] = value;
                if (commandSqlLogging) {
                    logger.debug(MODULE_NAME, `[PARAM] ${key}: ${type} = ${typeof value === 'string' && value.length > 50 ? value.substring(0, 50) + '...' : value}`);
                }
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
            const deleted = await withHandleRecovery(
                `delete ${dbName}`, () => poolUtil.unlink(dbPath));
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
            await withHandleRecovery(
                `rename ${oldName} → ${newName}`,
                () => poolUtil.renameFile(oldPath, newPath));
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

/**
 * Put `sourceName` in `targetName`'s place: the target's slot is freed and
 * the source's slot re-tagged with the target's path, in one pool metadata
 * update. No bytes are copied.
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
        () => poolUtil.atomicReplaceFile(
            `/databases/${sourceName}`, `/databases/${targetName}`));
    logger.info(MODULE_NAME, `✓ Replaced ${targetName} with ${sourceName}`);
    return { success: true };
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
 *              the chunks go straight into the pool's temp slot and no copy
 *              of the file exists anywhere.
 *   staging  → a `.dbs` envelope, which is validated in one pass and
 *              committed in another. It lands in an OPFS staging file the
 *              worker re-streams per pass.
 *
 * The session machinery itself is plane-neutral and lives in worker-common;
 * what a plane supplies is the opener. Plane 1 has no key, so the sink
 * writes plain pages and there is nothing to dispose.
 */
const importSessionHost = createImportSessionHost({
    async openDatabaseSink(dbName, plainSize) {
        if (!sqlite3 || !poolUtil) {
            throw new Error('SQLite not initialized');
        }
        await closeDatabase(dbName);
        return {
            sink: createDatabaseImportSink(dbName, plainSize, poolUtil, undefined, undefined),
            dispose() {
            },
        };
    },
    onDatabaseCommitted(dbName) {
        logger.info(MODULE_NAME, `✓ Imported ${dbName}`);
    },
});

/**
 * Multi-DB plain export — writes the complete `.dbs` envelope into an OPFS
 * staging file and returns its name. Plane 1 emits every file verbatim;
 * the codec itself is shared with plane 2, which decrypts on the way out.
 */
async function exportDatabasesToStagingOp(dbNames: string[]): Promise<string> {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    const staging = await exportDatabasesToStaging(dbNames, {
        poolUtil,
        closeDatabase,
    });
    return staging.name;
}

/**
 * Multi-DB plain import — consumes the `.dbs` envelope the session staged.
 * Two passes over the re-streamable File: validate the whole envelope
 * read-only (file count, tuple arity, page-aligned lengths, SQLite magic,
 * no premature EOF) before any destructive pool operation, then commit.
 *
 * `keepExisting` skips the commit pass's wipe: C# has already parked the
 * previous content under a suffixed name and will restore or drop it once
 * it has inspected what arrived.
 */
async function importDatabasesFromSessionOp(
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
    });
}

async function importDatabase(dbName: string, data: Uint8Array) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        logger.info(MODULE_NAME, `Importing database ${dbName} (${data.length} bytes)`);

        const dbPath = `/databases/${dbName}`;

        // Close database if open (SAHPool requirement).
        await closeDatabase(dbName);

        // The vendor pool validates the 'SQLite format 3' header and patches
        // byte 18 for WAL mode. Both are right here: everything this plane
        // writes is a plain SQLite file. Plane 2 forks the pool to skip them
        // for ciphertext, which would otherwise corrupt the AEAD tag on slot 0.
        poolUtil.importDb(dbPath, data);

        logger.info(MODULE_NAME, `✓ Imported database: ${dbName} (${data.length} bytes)`);

        // PoolImportResult.OK = 0
        return { rowsAffected: 0 };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to import database ${dbName}:`, error);
        throw error;
    }
}

// Plane 1 has no concept of an installed global encryption key (that lives
// in plane 2's vfs-prf key-registry). Stub to always return false so the
// branches in openDatabase / importDatabase that gate on it take the
// plain-disk path. The if-blocks themselves get tree-shaken by esbuild.
function hasGlobalKey(): boolean { return false; }

/**
 * Plane-1 verbatim export — copies raw OPFS bytes to a Uint8Array and
 * returns them. No mode-aware logic (rekey / encrypt require plane 2's
 * vfs-prf). Always closes the DB first for a consistent SAH snapshot.
 */
async function exportDatabase(
    dbName: string,
    _mode: "verbatim" | "plain" = "verbatim",
) {
    if (!sqlite3 || !poolUtil) {
        throw new Error("SQLite not initialized");
    }
    const dbPath = `/databases/${dbName}`;
    await closeDatabase(dbName);
    const raw: Uint8Array = poolUtil.exportFile(dbPath);
    logger.info(MODULE_NAME, `✓ Exported verbatim ${dbName}: ${raw.length}B`);
    return { rawBinary: true, data: raw };
}

// One staging write per this many bytes — small enough that the worker's
// transient JS-heap footprint stays negligible on memory-constrained
// mobile browsers, large enough that SAH read/write round-trips don't
// dominate export time.
const EXPORT_CHUNK_BYTES = 4 * 1024 * 1024;

/**
 * Plane-1 verbatim export into an OPFS staging file — the download-sized
 * sibling of <see cref="exportDatabase"/>. Copies the raw OPFS bytes in
 * EXPORT_CHUNK_BYTES slices via the pool's exportFileSlice (patch-provided,
 * see patches/@sqlite.org+sqlite-wasm+*.patch), so neither the worker nor
 * the main thread ever holds the whole file. Returns the staging file name;
 * the bridge lifts it as a disk-backed File for the anchor download and the
 * init-time sweep collects it next session.
 */
async function exportDatabaseToStaging(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }
    const dbPath = `/databases/${dbName}`;
    if (!poolUtil.getFileNames().includes(dbPath)) {
        throw new Error(`exportDbToStaging: no existing DB at ${dbPath}`);
    }
    await closeDatabase(dbName);

    const fileSize: number = poolUtil.getFileSize(dbPath);
    if (fileSize === 0) {
        throw new Error(`exportDbToStaging: ${dbName} is empty`);
    }

    const staging = await openExportStaging();
    try {
        for (let offset = 0; offset < fileSize; offset += EXPORT_CHUNK_BYTES) {
            const length = Math.min(EXPORT_CHUNK_BYTES, fileSize - offset);
            const chunk: Uint8Array = poolUtil.exportFileSlice(dbPath, offset, length);
            staging.write(chunk);
        }
        staging.finish();
    } catch (err) {
        await staging.abort();
        throw err;
    }
    logger.info(MODULE_NAME, `✓ Exported ${dbName} to staging (${fileSize}B)`);
    return { stagingFile: staging.name, fileSize };
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
