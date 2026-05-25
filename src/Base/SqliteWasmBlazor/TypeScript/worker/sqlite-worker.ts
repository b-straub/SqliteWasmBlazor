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

const requestQueue: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>[] = [];
const requestQueueEnqueuedAt = new WeakMap<MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>, number>();
const activeTransactions = new Map<string, boolean>();
let requestQueueProcessing = false;
const transactionQueueTimeoutMs = 300_000;
const deferredQueueRetryMs = 250;

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

        // Install vendor OPFS SAHPool VFS (plane 1 — no encryption).
        // Plane-2's worker bundle (SqliteWasmBlazor.Crypto) ships a forked
        // installer that adds per-page ChaCha20-Poly1305 encryption when a
        // global key is registered; this plane stays page-for-page identical
        // to the vendor implementation.
        //
        // Pool capacity: each DB occupies 1 slot for the main file; in
        // journal_mode=WAL it may also claim `.db-wal` and `.db-shm` slots
        // plus a transient `.db-journal` during the WAL mode transition
        // (~4 slots per active WAL DB). A POS-style offline workload can open
        // enough logical databases that 25 slots still trips "SAH pool is
        // full" during journal creation, so keep enough headroom for realistic
        // app data, temp journals, imports, and backup/export probes.
        poolUtil = await (sqlite3 as any).installOpfsSAHPoolVfs({
            initialCapacity: 64,
            directory: '/databases',
            name: 'opfs-sahpool',
            clearOnInit: false
        });
        setPoolUtil(poolUtil);

        // Grow pool if previously created with smaller capacity (initialCapacity only applies on first creation)
        await poolUtil.reserveMinimumCapacity(64);

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
self.onmessage = (event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>) => {
    requestQueueEnqueuedAt.set(event, Date.now());
    requestQueue.push(event);
    void processRequestQueue();
};

async function processRequestQueue() {
    if (requestQueueProcessing) {
        return;
    }

    requestQueueProcessing = true;
    try {
        for (;;) {
            const event = takeNextRequest();
            if (!event) {
                break;
            }

            await handleWorkerMessage(event);
        }
    } finally {
        requestQueueProcessing = false;
    }

    if (requestQueue.some((event, index) => !shouldDeferRequest(event, index))) {
        setTimeout(() => void processRequestQueue(), 0);
    } else if (requestQueue.length > 0) {
        setTimeout(() => void processRequestQueue(), deferredQueueRetryMs);
    }
}

function takeNextRequest() {
    for (let i = 0; i < requestQueue.length; i++) {
        const event = requestQueue[i];
        if (!shouldDeferRequest(event, i)) {
            return requestQueue.splice(i, 1)[0];
        }

        if (isTransactionQueueTimedOut(event)) {
            requestQueue.splice(i, 1);
            postDeferredTransactionTimeout(event);
            i--;
        }
    }

    return null;
}

function isTransactionQueueTimedOut(event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>) {
    const enqueuedAt = requestQueueEnqueuedAt.get(event);
    return enqueuedAt !== undefined && Date.now() - enqueuedAt >= transactionQueueTimeoutMs;
}

function postDeferredTransactionTimeout(event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>) {
    if ('type' in event.data) {
        return;
    }

    const request = event.data as WorkerRequest;
    const database = request.data?.database ?? '(unknown)';
    logger.error(MODULE_NAME, `Timed out waiting for active SQLite transaction to finish. Database=${database}`);
    const response: WorkerResponse = {
        id: request.id,
        data: {
            success: false,
            error: `Timed out waiting for active SQLite transaction to finish on database '${database}'.`
        }
    };

    self.postMessage(response);
}

function shouldDeferRequest(event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>, index: number) {
    const database = getRequestDatabase(event);
    if (!database) {
        return false;
    }

    const kind = transactionKind(event);
    for (let i = 0; i < index; i++) {
        if (kind !== 'commit' &&
            kind !== 'rollback' &&
            getRequestDatabase(requestQueue[i]) === database &&
            shouldDeferRequest(requestQueue[i], i)) {
            return true;
        }
    }

    return kind === 'begin' && activeTransactions.get(database) === true;
}

function getRequestDatabase(event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>) {
    if ('type' in event.data) {
        return null;
    }

    return (event.data as WorkerRequest).data?.database ?? null;
}

function transactionKind(event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>) {
    if ('type' in event.data) {
        return null;
    }

    const request = event.data as WorkerRequest;
    if (request.data?.type !== 'execute') {
        return null;
    }

    const normalized = String(request.data.sql || '')
        .trim()
        .replace(/;$/, '')
        .replace(/\s+/g, ' ')
        .toUpperCase();

    if (/^BEGIN(?: TRANSACTION)?(?: DEFERRED| IMMEDIATE| EXCLUSIVE)?$/.test(normalized)) {
        return 'begin';
    }

    if (normalized === 'COMMIT' || normalized === 'END') {
        return 'commit';
    }

    if (normalized === 'ROLLBACK') {
        return 'rollback';
    }

    return null;
}

async function handleWorkerMessage(event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string; assetRoot?: string }>) {
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
    const dbName = data?.database;
    const txKind = transactionKind(event);

    try {
        const result = await handleRequest(data, binaryPayload, binaryHeader);
        if (txKind === 'begin' && dbName) {
            activeTransactions.set(dbName, true);
        } else if ((txKind === 'commit' || txKind === 'rollback') && dbName) {
            activeTransactions.set(dbName, false);
        }

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
    } finally {
        if ((txKind === 'commit' || txKind === 'rollback') && dbName) {
            activeTransactions.set(dbName, false);
        }
    }
}

async function handleRequest(data: WorkerRequest['data'], binaryPayload?: ArrayBuffer, binaryHeader?: ArrayBuffer) {
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
            db.exec("PRAGMA foreign_keys = ON;");
            logger.debug(
                MODULE_NAME,
                `Set PRAGMAs for ${dbName} (encrypted: page_size=4096, journal_mode=WAL, foreign_keys=ON)`
            );
        } else {
            // Plain DBs: existing behavior unchanged.
            db.exec("PRAGMA locking_mode = exclusive;");
            db.exec("PRAGMA journal_mode = WAL;");
            db.exec("PRAGMA synchronous = FULL;");
            db.exec("PRAGMA foreign_keys = ON;");
            logger.debug(
                MODULE_NAME,
                `Set PRAGMAs for ${dbName} (locking_mode=exclusive, journal_mode=WAL, synchronous=FULL, foreign_keys=ON)`
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
    // Match the main table for common SELECT/INSERT/UPDATE/DELETE shapes.
    // This is a heuristic for declared column type metadata, not SQL parsing.
    const match = sql.match(/\bFROM\s+["']?(\w+)["']?/i)
        ?? sql.match(/\bUPDATE\s+["']?(\w+)["']?/i)
        ?? sql.match(/\bINSERT\s+(?:OR\s+\w+\s+)?INTO\s+["']?(\w+)["']?/i)
        ?? sql.match(/\bDELETE\s+FROM\s+["']?(\w+)["']?/i);
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

function filterParametersForSql(sql: string, parameters: Record<string, any>): Record<string, any> {
    if (Object.keys(parameters).length === 0) {
        return parameters;
    }

    const filtered: Record<string, any> = {};
    for (const [key, value] of Object.entries(parameters)) {
        if (!key || sqlContainsParameter(sql, key)) {
            filtered[key] = value;
        }
    }

    return filtered;
}

function sqlContainsParameter(sql: string, parameterName: string): boolean {
    if (!isParameterPrefix(parameterName[0])) {
        return false;
    }

    let index = 0;
    while (index < sql.length) {
        const ch = sql[index];
        const next = sql[index + 1];

        if (ch === '\'' || ch === '"') {
            index = skipQuoted(sql, index, ch);
            continue;
        }

        if (ch === '-' && next === '-') {
            index += 2;
            while (index < sql.length && sql[index] !== '\n' && sql[index] !== '\r') {
                index++;
            }
            continue;
        }

        if (ch === '/' && next === '*') {
            index += 2;
            while (index + 1 < sql.length && !(sql[index] === '*' && sql[index + 1] === '/')) {
                index++;
            }
            index = Math.min(index + 2, sql.length);
            continue;
        }

        if (sql.startsWith(parameterName, index) &&
            !isParameterNamePart(sql[index + parameterName.length])) {
            return true;
        }

        index++;
    }

    return false;
}

function isParameterPrefix(ch: string | undefined): boolean {
    return ch === '@' || ch === '$' || ch === ':';
}

function isParameterNamePart(ch: string | undefined): boolean {
    return ch !== undefined && /[A-Za-z0-9_]/.test(ch);
}

function getPrimaryStatementKeyword(sql: string): string {
    let index = skipSqlTrivia(sql, 0);
    let token = readSqlKeyword(sql, index);
    if (!token) {
        return '';
    }

    if (token.keyword !== 'WITH') {
        return token.keyword;
    }

    index = skipSqlTrivia(sql, token.nextIndex);
    token = readSqlKeyword(sql, index);
    if (token?.keyword === 'RECURSIVE') {
        index = skipSqlTrivia(sql, token.nextIndex);
    }

    while (index < sql.length) {
        const asIndex = findTopLevelKeyword(sql, index, 'AS');
        if (asIndex < 0) {
            return 'WITH';
        }

        index = skipSqlTrivia(sql, asIndex + 2);

        const materialized = readSqlKeyword(sql, index);
        if (materialized?.keyword === 'MATERIALIZED') {
            index = skipSqlTrivia(sql, materialized.nextIndex);
        } else if (materialized?.keyword === 'NOT') {
            const maybeMaterialized = readSqlKeyword(sql, skipSqlTrivia(sql, materialized.nextIndex));
            if (maybeMaterialized?.keyword === 'MATERIALIZED') {
                index = skipSqlTrivia(sql, maybeMaterialized.nextIndex);
            }
        }

        index = skipSqlTrivia(sql, index);
        if (sql[index] !== '(') {
            return 'WITH';
        }

        index = skipBalancedParentheses(sql, index);
        index = skipSqlTrivia(sql, index);

        if (sql[index] === ',') {
            index = skipSqlTrivia(sql, index + 1);
            continue;
        }

        return readSqlKeyword(sql, index)?.keyword ?? 'WITH';
    }

    return 'WITH';
}

function skipSqlTrivia(sql: string, startIndex: number): number {
    let index = startIndex;
    while (index < sql.length) {
        const ch = sql[index];
        const next = sql[index + 1];

        if (/\s/.test(ch)) {
            index++;
            continue;
        }

        if (ch === '-' && next === '-') {
            index += 2;
            while (index < sql.length && sql[index] !== '\n' && sql[index] !== '\r') {
                index++;
            }
            continue;
        }

        if (ch === '/' && next === '*') {
            index += 2;
            while (index + 1 < sql.length && !(sql[index] === '*' && sql[index + 1] === '/')) {
                index++;
            }
            index = Math.min(index + 2, sql.length);
            continue;
        }

        return index;
    }

    return index;
}

function readSqlKeyword(sql: string, startIndex: number): { keyword: string; nextIndex: number } | null {
    let index = startIndex;
    if (!isIdentifierStart(sql[index])) {
        return null;
    }

    index++;
    while (index < sql.length && isIdentifierPart(sql[index])) {
        index++;
    }

    return {
        keyword: sql.slice(startIndex, index).toUpperCase(),
        nextIndex: index
    };
}

function findTopLevelKeyword(sql: string, startIndex: number, keyword: string): number {
    let index = startIndex;
    let depth = 0;

    while (index < sql.length) {
        const ch = sql[index];
        const next = sql[index + 1];

        if (ch === '\'' || ch === '"') {
            index = skipQuoted(sql, index, ch);
            continue;
        }

        if (ch === '-' && next === '-') {
            index = skipSqlTrivia(sql, index);
            continue;
        }

        if (ch === '/' && next === '*') {
            index = skipSqlTrivia(sql, index);
            continue;
        }

        if (ch === '(') {
            depth++;
            index++;
            continue;
        }

        if (ch === ')') {
            depth = Math.max(0, depth - 1);
            index++;
            continue;
        }

        if (depth === 0 && isIdentifierStart(ch)) {
            const token = readSqlKeyword(sql, index);
            if (token?.keyword === keyword) {
                return index;
            }

            index = token?.nextIndex ?? index + 1;
            continue;
        }

        index++;
    }

    return -1;
}

function skipBalancedParentheses(sql: string, startIndex: number): number {
    let index = startIndex;
    let depth = 0;

    while (index < sql.length) {
        const ch = sql[index];
        const next = sql[index + 1];

        if (ch === '\'' || ch === '"') {
            index = skipQuoted(sql, index, ch);
            continue;
        }

        if (ch === '-' && next === '-') {
            index = skipSqlTrivia(sql, index);
            continue;
        }

        if (ch === '/' && next === '*') {
            index = skipSqlTrivia(sql, index);
            continue;
        }

        if (ch === '(') {
            depth++;
        } else if (ch === ')') {
            depth--;
            if (depth === 0) {
                return index + 1;
            }
        }

        index++;
    }

    return index;
}

function skipQuoted(sql: string, startIndex: number, quote: string): number {
    let index = startIndex + 1;
    while (index < sql.length) {
        if (sql[index] === quote) {
            if (sql[index + 1] === quote) {
                index += 2;
                continue;
            }

            return index + 1;
        }

        index++;
    }

    return index;
}

function isIdentifierStart(ch: string | undefined): boolean {
    return ch !== undefined && /[A-Za-z_]/.test(ch);
}

function isIdentifierPart(ch: string | undefined): boolean {
    return ch !== undefined && /[A-Za-z0-9_]/.test(ch);
}

function reportsRowsAffected(keyword: string): boolean {
    return keyword === 'INSERT'
        || keyword === 'UPDATE'
        || keyword === 'DELETE'
        || keyword === 'REPLACE'
        || keyword === 'CREATE';
}

function shouldReadColumnMetadata(sql: string, keyword: string, result: any[] | undefined): boolean {
    return (result?.length ?? 0) > 0
        || keyword === 'SELECT'
        || keyword === 'WITH'
        || keyword === 'PRAGMA'
        || /\bRETURNING\b/i.test(sql);
}

function isPragmaEncodingQuery(sql: string): boolean {
    return /^\s*PRAGMA\s+encoding\s*;?\s*$/i.test(sql);
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
        const convertedParams = filterParametersForSql(
            sql,
            convertParametersForBinding(parameters, binaryPayload));

        // Execute SQL - use returnValue to get the result
        let result = db.exec({
            sql: sql,
            bind: Object.keys(convertedParams).length > 0 ? convertedParams : undefined,
            returnValue: 'resultRows',
            rowMode: 'array'
        });
        if (result.length === 0 && isPragmaEncodingQuery(sql)) {
            result = [['UTF-8']];
        }

        logger.debug(MODULE_NAME, 'SQL executed successfully, rows:', result?.length || 0);

        // Extract column metadata if there are results
        let columnNames: string[] = [];
        let columnTypes: string[] = [];
        const statementKeyword = getPrimaryStatementKeyword(sql);

        if (shouldReadColumnMetadata(sql, statementKeyword, result)) {
            const stmt = db.prepare(sql);
            try {
                const colCount = stmt.columnCount;

                // Try to get declared types from the statement's main table.
                let tableSchema: Map<string, string> | null = null;
                const tableName = extractTableName(sql);
                if (tableName) {
                    tableSchema = getTableSchema(db, dbName, tableName);
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

        if (reportsRowsAffected(statementKeyword)) {

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
    activeTransactions.delete(dbName);
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
