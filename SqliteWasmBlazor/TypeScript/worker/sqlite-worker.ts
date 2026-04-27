// sqlite-worker.ts
// Web Worker for executing SQL with sqlite-wasm + OPFS SAHPool VFS
// SAHPool provides synchronous OPFS access in worker context

import sqlite3InitModule from '@sqlite.org/sqlite-wasm';
import { logger } from './sqlite-logger';
import { pack, unpack } from 'msgpackr';
import { registerEFCoreFunctions } from './ef-core-functions';
import {
    openDatabases, pragmasSet, schemaCache,
    MODULE_NAME, bigIntUnpackr,
    setSqlite3, setPoolUtil, setBaseHref
} from './worker-state';
import { bulkInsertRows, type V2Header } from './bulk-ops';
import { deltaExportEncrypted, deltaImportEncrypted, bulkRotateKeyV2 } from './crypto-ops';
import { installOpfsSAHPoolVfs as installPrfVfs } from './vfs-prf/sahpool-prf-vfs';
import {
    registerKeyForPath,
    getKeyForPath,
    clearKeyForPath,
    isPathEncrypted,
} from './vfs-prf/key-registry';
import { rekeySlots } from './vfs-prf/rekey';

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
            // Optional binaryPayload carries a MessagePack-serialized VfsKeyHeader
            // (C# SqliteWasmBlazor.VfsKeyHeader). Its presence switches the DB to
            // the encrypted VFS path. Matches the V2CryptoHeader envelope shape
            // used by deltaExportEncrypted / deltaImportEncrypted so every C# →
            // worker path that ships key material uses a versioned envelope.
            return await openDatabase(
                database!,
                binaryPayload ? unpackVfsKeyHeader(new Uint8Array(binaryPayload)) : undefined
            );

        case 'registerEncryptionKey':
            // PRF / DomainKeys path: caller derived a 32-byte key in C# from
            // SqliteWasmBlazor.Crypto's SecureKeyCache and is shipping it as a VfsKeyHeader
            // envelope (same shape as 'open' with a key, minus the actual open).
            // The key is stored in the per-path registry so the next plain
            // 'open' message picks it up at xOpen.
            if (!binaryPayload) {
                throw new Error('registerEncryptionKey requires binaryPayload (VfsKeyHeader)');
            }
            return registerEncryptionKey(
                database!,
                unpackVfsKeyHeader(new Uint8Array(binaryPayload))
            );

        case 'clearEncryptionKey':
            // Symmetric to registerEncryptionKey: drops the registry entry and
            // wipes the buffer in place. Called by domain-key expiry handlers
            // (KeyExpired subscriptions) and explicit lock UI.
            return clearEncryptionKey(database!);

        case 'execute':
            return await executeSql(database!, sql!, parameters || {});

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
            // Slot-rekey primitive with three flavours selected by `mode`:
            //   verbatim → raw OPFS bytes (slot-format ciphertext or plain pages)
            //   plain    → decrypt under registered K_old → plain pages
            //   rekey    → decrypt under K_old, re-encrypt under caller-supplied
            //              K_new (binaryPayload carries VfsKeyHeader)
            const mode = (data as any).mode as 'verbatim' | 'plain' | 'rekey';
            let newKey: Uint8Array | undefined;
            if (mode === 'rekey') {
                if (!binaryPayload) {
                    throw new Error(
                        "exportDb mode='rekey' requires binaryPayload (VfsKeyHeader for K_new)");
                }
                newKey = unpackVfsKeyHeader(new Uint8Array(binaryPayload));
            }
            return await exportDatabase(database!, mode, newKey);
        }

        case 'importRows':
            if (!binaryPayload) {
                throw new Error('importRows requires binaryPayload (V2 MessagePack)');
            }
            return importRows(database!, new Uint8Array(binaryPayload), data as any);

        case 'deltaExportEncrypted':
            if (!binaryPayload) {
                throw new Error('deltaExportEncrypted requires binaryPayload (V2CryptoHeader)');
            }
            return await deltaExportEncrypted(database!, new Uint8Array(binaryPayload), data as any);

        case 'deltaImportEncrypted':
            if (!binaryPayload || !binaryHeader) {
                throw new Error('deltaImportEncrypted requires binaryPayload (V2CryptoHeader) + binaryHeader (ShadowRowGroup)');
            }
            return await deltaImportEncrypted(
                database!,
                new Uint8Array(binaryPayload),
                new Uint8Array(binaryHeader),
                data as any
            );

        case 'bulkRotateKeyV2':
            if (!binaryPayload || !binaryHeader) {
                throw new Error('bulkRotateKeyV2 requires binaryPayload (oldV2CryptoHeader) + binaryHeader (newV2CryptoHeader)');
            }
            return await bulkRotateKeyV2(
                database!,
                new Uint8Array(binaryPayload),
                new Uint8Array(binaryHeader),
                data as any
            );

        default:
            throw new Error(`Unknown request type: ${type}`);
    }
}

async function openDatabase(dbName: string, encryptionKey?: Uint8Array) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const dbPath = `/databases/${dbName}`;

    // Register the key BEFORE opening the DB. The VFS's xOpen reads the
    // registry to decide whether to stamp `file.key` on the opened file.
    // A re-open of an already-open DB with a different key is a caller
    // error; we don't support swapping keys for a live handle.
    if (encryptionKey) {
        if (encryptionKey.length !== 32) {
            throw new Error(
                `encryptionKey must be 32 bytes, got ${encryptionKey.length}`
            );
        }
        registerKeyForPath(dbPath, encryptionKey);
    }

    // Check if database needs to be opened
    let db = openDatabases.get(dbName);
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
                `✓ Opened database: ${dbName} with OPFS SAHPool${isPathEncrypted(dbPath) ? ' (encrypted)' : ''}`
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
            // If we registered a key but the open failed, clear it to avoid
            // a stale entry on a future retry.
            if (encryptionKey) {
                clearKeyForPath(dbPath);
            }
            logger.error(MODULE_NAME, `Failed to open database ${dbName}:`, error);
            throw error;
        }
    }

    // Always check if PRAGMAs need to be set (even if database was already open)
    // This handles the case where database was closed and reopened
    if (!pragmasSet.has(dbName)) {
        if (isPathEncrypted(dbPath)) {
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
 * Register a per-path encryption key in the worker's key registry without
 * opening the DB. The next plain 'open' for the same dbName will see the
 * registered key in xOpen and route through the encrypted VFS path.
 *
 * Used by the SqliteWasmBlazor.Crypto DomainKeys flow: C# derives the key in
 * SecureKeyCache, hands it to the bridge as a span (no managed copy), the
 * bridge ships it here as a VfsKeyHeader envelope, and the page then
 * resolves a normal DbContext factory which triggers the no-key 'open'.
 *
 * Atomic verify: when an existing DB file is present at this path, the
 * worker AEAD-tests slot 0 against the freshly registered key. On mismatch
 * the registry entry is cleared so a known-wrong key never sees a write.
 * The outcome is encoded in `rowsAffected` (0 = no existing DB, 1 = match,
 * 2 = wrong key); the C# bridge maps this to VfsKeyInstallResult.
 */
function registerEncryptionKey(dbName: string, key: Uint8Array) {
    if (key.length !== 32) {
        throw new Error(`encryptionKey must be 32 bytes, got ${key.length}`);
    }
    const dbPath = `/databases/${dbName}`;
    registerKeyForPath(dbPath, key);

    const result = poolUtil.verifyEncryptionKey(dbPath);
    if (result === 'wrongKey') {
        clearKeyForPath(dbPath);
        logger.warn(
            MODULE_NAME,
            `Registered key rejected by AEAD on slot 0 of ${dbPath}; cleared registry.`
        );
        return { rowsAffected: 2 };
    }

    logger.debug(
        MODULE_NAME,
        `Registered encryption key for ${dbPath} (verify: ${result})`
    );
    return { rowsAffected: result === 'match' ? 1 : 0 };
}

/**
 * Drops a previously-registered encryption key. Called by domain-key
 * expiry handlers and explicit lock UI. Idempotent: safe to call when no
 * key is registered.
 */
function clearEncryptionKey(dbName: string) {
    const dbPath = `/databases/${dbName}`;
    clearKeyForPath(dbPath);
    logger.debug(MODULE_NAME, `Cleared encryption key for ${dbPath}`);
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
    const decoded = unpack(headerBytes);
    if (!Array.isArray(decoded) || decoded.length < 2) {
        throw new Error('VfsKeyHeader: invalid MessagePack envelope');
    }
    const [version, key, aadVersion] = decoded as [number, Uint8Array, string];
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
    return key;
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
function convertParametersForBinding(parameters: Record<string, any>): Record<string, any> {
    const converted: Record<string, any> = {};

    for (const [key, paramData] of Object.entries(parameters)) {
        // Handle new format with type metadata
        if (paramData && typeof paramData === 'object' && 'value' in paramData && 'type' in paramData) {
            const { value, type } = paramData;

            if (value === null || value === undefined) {
                converted[key] = null;
                logger.debug(MODULE_NAME, `[PARAM] ${key}: null`);
            }
            else if (type === 'blob' && typeof value === 'string') {
                // Decode base64 to Uint8Array for BLOB binding
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

async function executeSql(dbName: string, sql: string, parameters: Record<string, any>) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    try {
        logger.debug(MODULE_NAME, 'Executing SQL:', sql.substring(0, 100));

        // Convert parameters with type metadata for proper SQLite binding
        const convertedParams = convertParametersForBinding(parameters);

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
        // Wipe the ChaCha20-Poly1305 key (if this DB was encrypted). No-op
        // for non-encrypted DBs. clearKeyForPath zero-fills the buffer
        // before removing the registry entry.
        clearKeyForPath(`/databases/${dbName}`);
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
        if (opaque && isPathEncrypted(dbPath)) {
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
 * Snapshot the registered key for the given path before any code path that
 * clears it (e.g. closeDatabase). Returns a fresh copy so the caller can
 * use the bytes after the registry has been wiped.
 */
function snapshotKeyForPath(dbPath: string): Uint8Array | undefined {
    const live = getKeyForPath(dbPath);
    if (live === undefined) {
        return undefined;
    }
    const copy = new Uint8Array(live.length);
    copy.set(live);
    return copy;
}

/**
 * Slot-rekey primitive — the single export entry point. Always closes the
 * DB first for a consistent SAH snapshot, then post-processes the raw bytes
 * according to mode:
 *   verbatim → return raw bytes (plain pages or slot-format ciphertext as-is)
 *   plain    → rekeySlots(raw, sourceKey, undefined) → plain SQLite pages
 *   rekey    → rekeySlots(raw, sourceKey, newKey)    → slot ciphertext under K_new
 *
 * AAD binds dbPath, so REKEY output must be re-imported to the same DB name.
 */
async function exportDatabase(
    dbName: string,
    mode: 'verbatim' | 'plain' | 'rekey',
    newKey?: Uint8Array,
) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        const dbPath = `/databases/${dbName}`;
        // Verbatim does not touch slot encoding, so the registered key is
        // irrelevant. Plain and rekey both need K_old to decrypt the source
        // slots, snapshotted before closeDatabase clears the registry.
        const sourceKey = mode === 'verbatim' ? undefined : snapshotKeyForPath(dbPath);

        await closeDatabase(dbName);

        const raw: Uint8Array = poolUtil.exportFile(dbPath);

        if (mode === 'verbatim') {
            logger.info(MODULE_NAME, `✓ Exported verbatim ${dbName}: ${raw.length}B`);
            return { rawBinary: true, data: raw };
        }

        const targetKey = mode === 'rekey' ? newKey : undefined;
        const out = rekeySlots(raw, dbPath, sourceKey, targetKey);

        logger.info(
            MODULE_NAME,
            `✓ Exported ${mode} ${dbName}: ${raw.length}B → ${out.length}B`,
        );

        return { rawBinary: true, data: out };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to export ${mode} ${dbName}:`, error);
        throw error;
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

    const header = objects[0] as V2Header;
    const rows = objects.slice(1) as any[][];
    const conflictStrategy = metadata.conflictStrategy ?? header[6] ?? 0;

    return bulkInsertRows(db, header, rows, conflictStrategy, 'importRows');
}

// Bulk import/export and crypto operations are in separate modules:
// - bulk-ops.ts: V2 MessagePack format, prepared statement loop
// - crypto-ops.ts: V2 encrypted export/import/rotate with three-layer tamper detection
// - type-conversion.ts: MessagePack ↔ SQLite value conversion
