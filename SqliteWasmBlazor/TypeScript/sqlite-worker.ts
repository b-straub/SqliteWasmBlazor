// sqlite-worker.ts
// Web Worker for executing SQL with sqlite-wasm + OPFS SAHPool VFS
// SAHPool provides synchronous OPFS access in worker context

import sqlite3InitModule, { type SqlValue } from '@sqlite.org/sqlite-wasm';
import { logger } from './sqlite-logger';
import { pack, unpack, Unpackr } from 'msgpackr';

// Unpackr preserving int64 as BigInt — JS Number loses precision for values > 2^53-1
const bigIntUnpackr = new Unpackr({ int64AsType: 'bigint' });
import { registerEFCoreFunctions } from './ef-core-functions';

interface WorkerRequest {
    id: number;
    data: {
        type: string;
        database?: string;
        sql?: string;
        parameters?: Record<string, any>;
        key?: string; // SQLCipher key (plaintext fallback)
        encryptedKey?: { iv: number[]; ct: number[]; senderPub: number[] }; // ECDH-wrapped key
    };
    binaryPayload?: ArrayBuffer;
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

let sqlite3: any;
let poolUtil: any;
const openDatabases = new Map<string, any>();
const pragmasSet = new Set<string>(); // Track which databases have PRAGMAs configured

// ECDH key pair for encrypted key exchange with main thread (lazy — only generated on first encrypted open)
let workerKeyPair: CryptoKeyPair | null = null;

// Cache table schemas: Map<tableName, Map<columnName, columnType>>
const schemaCache = new Map<string, Map<string, string>>();

const MODULE_NAME = 'SQLite Worker';

// Store base href from main thread
let baseHref = '/';

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
                // Tell sqlite-wasm where to find the wasm file using base href
                if (path.endsWith('.wasm')) {
                    return `${baseHref}_content/SqliteWasmBlazor/${path}`;
                }
                return path;
            }
        };
        sqlite3 = await (sqlite3InitModule as (options: typeof initOptions) => Promise<typeof sqlite3>)(initOptions);

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

        // Install OPFS SAHPool VFS
        poolUtil = await sqlite3.installOpfsSAHPoolVfs({
            initialCapacity: 10,
            directory: '/databases',
            name: 'opfs-sahpool',
            clearOnInit: false
        });

        // Grow pool if previously created with smaller capacity (initialCapacity only applies on first creation)
        await poolUtil.reserveMinimumCapacity(10);

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
self.onmessage = async (event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string } | { type: 'initEncryption' }>) => {
    // Handle initialization with base href
    if ('type' in event.data && event.data.type === 'init' && 'baseHref' in event.data) {
        baseHref = event.data.baseHref;
        // Start initialization after receiving base href
        await initializeSQLite();
        return;
    }

    // Handle log level changes (no response needed)
    if ('type' in event.data && event.data.type === 'setLogLevel' && 'level' in event.data) {
        logger.setLogLevel(event.data.level);
        return;
    }

    // Lazy ECDH key exchange — generate worker key pair on first encrypted open request
    if ('type' in event.data && event.data.type === 'initEncryption') {
        workerKeyPair = await crypto.subtle.generateKey(
            { name: 'ECDH', namedCurve: 'P-256' },
            false, // private key non-extractable
            ['deriveKey']
        );
        const rawPub = await crypto.subtle.exportKey('raw', workerKeyPair.publicKey);
        self.postMessage({ type: 'workerPublicKey', pub: new Uint8Array(rawPub) });
        return;
    }

    // Handle regular requests
    const { id, data, binaryPayload } = event.data as WorkerRequest;

    try {
        const result = await handleRequest(data, binaryPayload);

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

async function handleRequest(data: WorkerRequest['data'], binaryPayload?: ArrayBuffer) {
    const { type, database, sql, parameters } = data;

    switch (type) {
        case 'open': {
            if (data.encryptedKey) {
                // Decrypt ECDH-wrapped key before opening
                if (!workerKeyPair) {
                    throw new Error('Encrypted key received but ECDH key pair not initialized');
                }
                const { iv, ct, senderPub } = data.encryptedKey;
                const senderPubKey = await crypto.subtle.importKey(
                    'raw',
                    new Uint8Array(senderPub),
                    { name: 'ECDH', namedCurve: 'P-256' },
                    false,
                    []
                );
                const sharedKey = await crypto.subtle.deriveKey(
                    { name: 'ECDH', public: senderPubKey },
                    workerKeyPair.privateKey,
                    { name: 'AES-GCM', length: 256 },
                    false,
                    ['decrypt']
                );
                const ptBytes = await crypto.subtle.decrypt(
                    { name: 'AES-GCM', iv: new Uint8Array(iv) },
                    sharedKey,
                    new Uint8Array(ct)
                );
                const password = new TextDecoder().decode(ptBytes);
                return await openDatabase(database!, password);
            }
            return await openDatabase(database!, data.key);
        }

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
            return await importDatabase(database!, new Uint8Array(binaryPayload));

        case 'exportDb':
            return await exportDatabase(database!);

        case 'bulkImport':
            if (!binaryPayload) {
                throw new Error('bulkImport requires binaryPayload');
            }
            return await bulkImport(
                database!,
                new Uint8Array(binaryPayload),
                (data as any).conflictStrategy ?? 0
            );

        case 'bulkImportRaw':
            if (!binaryPayload) {
                throw new Error('bulkImportRaw requires binaryPayload');
            }
            return await bulkImportRaw(
                database!,
                new Uint8Array(binaryPayload),
                data as any
            );

        case 'bulkExport':
            return await bulkExport(database!, data as any);

        default:
            throw new Error(`Unknown request type: ${type}`);
    }
}

async function openDatabase(dbName: string, key?: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    // Check if database needs to be opened
    let db = openDatabases.get(dbName);
    if (!db) {
        try {
            // Use OpfsSAHPoolDb from the pool utility
            // Wrap in timeout to detect multi-tab lock conflicts
            const dbPath = `/databases/${dbName}`;
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
            logger.info(MODULE_NAME, `✓ Opened database: ${dbName} with OPFS SAHPool`);

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
        // Apply SQLCipher key before WAL PRAGMAs.
        // NOTE: PRAGMA key does not support parameterized queries in the wasm-sqlite3 API;
        // string interpolation with single-quote doubling is the correct and only approach.
        if (key) {
            db.exec(`PRAGMA key = '${key.replace(/'/g, "''")}';`);
            logger.debug(MODULE_NAME, `Applied encryption key for ${dbName}`);
        }
        // WAL mode with OPFS requires exclusive locking mode (SQLite 3.47+)
        // Must be set BEFORE activating WAL mode
        db.exec("PRAGMA locking_mode = exclusive;");
        db.exec("PRAGMA journal_mode = WAL;");
        db.exec("PRAGMA synchronous = FULL;");
        pragmasSet.add(dbName);
        logger.debug(MODULE_NAME, `Set PRAGMAs for ${dbName} (locking_mode=exclusive, journal_mode=WAL, synchronous=FULL)`);

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

async function importDatabase(dbName: string, data: Uint8Array) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        logger.info(MODULE_NAME, `Importing database ${dbName} (${data.length} bytes)`);

        // Close database if open (SAHPool requirement)
        await closeDatabase(dbName);

        // Import the raw database file into OPFS SAHPool
        const dbPath = `/databases/${dbName}`;
        poolUtil.importDb(dbPath, data);

        logger.info(MODULE_NAME, `✓ Imported database: ${dbName} (${data.length} bytes)`);

        return { success: true, rowsAffected: data.length };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to import database ${dbName}:`, error);
        throw error;
    }
}

async function exportDatabase(dbName: string) {
    if (!sqlite3 || !poolUtil) {
        throw new Error('SQLite not initialized');
    }

    try {
        logger.info(MODULE_NAME, `Exporting database ${dbName}`);

        // Close database for consistent snapshot (SAHPool requirement)
        await closeDatabase(dbName);

        // Export the raw database file from OPFS SAHPool
        const dbPath = `/databases/${dbName}`;
        const data: Uint8Array = poolUtil.exportFile(dbPath);

        logger.info(MODULE_NAME, `✓ Exported database: ${dbName} (${data.length} bytes)`);

        return { rawBinary: true, data };
    } catch (error) {
        logger.error(MODULE_NAME, `Failed to export database ${dbName}:`, error);
        throw error;
    }
}

// ============================================================================
// Bulk Import/Export (V2 MessagePack format — worker-side prepared statement loop)
// ============================================================================

interface V2Header {
    0: string;      // magic "SWBV2"
    1: string;      // schemaHash
    2: string;      // dataType
    3: string | null;// appIdentifier
    4: string;      // exportedAt (ISO 8601 string)
    5: number;      // recordCount
    6: number;      // mode: 0=Seed, 1=Delta
    7: string;      // tableName
    8: string[][];  // columns: [[name, sqlType, csharpType], ...]
    9: string;      // primaryKeyColumn
}

/**
 * Convert a value from MessagePack wire format to SQLite-compatible value.
 * Uses csharpType from column metadata to determine conversion.
 */
function convertValueForSqlite(value: any, csharpType: string, sqlType: string): SqlValue {
    if (value === null || value === undefined) {
        return null;
    }

    // Strip nullable suffix for matching
    const baseType = csharpType.endsWith('?') ? csharpType.slice(0, -1) : csharpType;

    switch (baseType) {
        case 'Guid': {
            // MessagePack-CSharp serializes Guid as 36-char string "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
            if (sqlType === 'BLOB') {
                // Convert to 16-byte Uint8Array matching .NET Guid.ToByteArray() layout:
                // Groups 1-3 are little-endian, groups 4-5 are big-endian
                const hex = (value as string).replace(/-/g, '');
                const bytes = new Uint8Array(16);
                // Group 1 (4 bytes, LE): hex[0..7] reversed
                bytes[0] = parseInt(hex.substring(6, 8), 16);
                bytes[1] = parseInt(hex.substring(4, 6), 16);
                bytes[2] = parseInt(hex.substring(2, 4), 16);
                bytes[3] = parseInt(hex.substring(0, 2), 16);
                // Group 2 (2 bytes, LE): hex[8..11] reversed
                bytes[4] = parseInt(hex.substring(10, 12), 16);
                bytes[5] = parseInt(hex.substring(8, 10), 16);
                // Group 3 (2 bytes, LE): hex[12..15] reversed
                bytes[6] = parseInt(hex.substring(14, 16), 16);
                bytes[7] = parseInt(hex.substring(12, 14), 16);
                // Groups 4-5 (8 bytes, BE): hex[16..31] as-is
                for (let i = 8; i < 16; i++) {
                    bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
                }
                return bytes as any;
            }
            // TEXT column: pass string as-is
            return String(value);
        }

        case 'DateTime':
            // MessagePack-CSharp: Timestamp ext (-1) → msgpackr: Date object
            if (value instanceof Date) {
                return value.toISOString();
            }
            return String(value);

        case 'DateTimeOffset':
            // MessagePack-CSharp: array [DateTime, short(offset minutes)]
            // msgpackr: [Date, number]
            if (Array.isArray(value) && value.length === 2 && value[0] instanceof Date) {
                return value[0].toISOString();
            }
            if (value instanceof Date) {
                return value.toISOString();
            }
            return String(value);

        case 'TimeSpan':
            // MessagePack-CSharp serializes as int64 (Ticks)
            if (sqlType === 'TEXT') {
                // Convert Ticks to .NET TimeSpan string format: [d.]hh:mm:ss[.fffffff]
                const ticks = Number(value);
                const negative = ticks < 0;
                const absTicks = Math.abs(ticks);
                const totalSeconds = Math.floor(absTicks / 10000000);
                const fraction = absTicks % 10000000;
                const days = Math.floor(totalSeconds / 86400);
                const hours = Math.floor((totalSeconds % 86400) / 3600);
                const minutes = Math.floor((totalSeconds % 3600) / 60);
                const seconds = totalSeconds % 60;
                const sign = negative ? '-' : '';
                const daysPart = days > 0 ? `${days}.` : '';
                const fractionPart = fraction > 0 ? `.${fraction.toString().padStart(7, '0')}` : '';
                return `${sign}${daysPart}${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}${fractionPart}`;
            }
            // INTEGER column: store as ticks directly
            return Number(value);

        case 'Boolean':
            return value ? 1 : 0;

        case 'String':
            return String(value);

        case 'Decimal':
            // MessagePack-CSharp: string representation → pass through as TEXT
            return String(value);

        case 'Int16':
        case 'Int32':
        case 'Byte':
        case 'UInt32':
            return Number(value);

        case 'Int64':
        case 'UInt64':
            // Bind as text to avoid int64 precision loss at JS↔WASM boundary.
            // SQLite INTEGER affinity coerces text→int64 correctly in C code.
            return String(value);

        case 'Double':
        case 'Single':
            return Number(value);

        case 'Char':
        case 'UInt16':
            // MessagePack-CSharp: char as uint16 → msgpackr: number
            // SQLite stores as TEXT (single character)
            if (typeof value === 'number') {
                return String.fromCharCode(value);
            }
            return String(value);

        case 'Enum':
            // MessagePack-CSharp: enum as underlying int → msgpackr: number
            return Number(value);

        case 'JsonArray':
            // EF Core JSON value converter: Array → JSON.stringify for TEXT column
            if (Array.isArray(value)) {
                return JSON.stringify(value);
            }
            return String(value);

        case 'ByteArray':
            // Already Uint8Array from msgpackr
            return value as any;

        default:
            logger.warn(MODULE_NAME, `convertValueForSqlite: unhandled type "${csharpType}", passing through`);
            return value as SqlValue;
    }
}

/**
 * Convert a SQLite value back to MessagePack-CSharp wire format for export.
 * This ensures exported files are compatible with C#'s MessagePackSerializer.Deserialize.
 */
function convertValueFromSqlite(value: any, csharpType: string, sqlType: string): any {
    if (value === null || value === undefined) {
        return null;
    }

    const baseType = csharpType.endsWith('?') ? csharpType.slice(0, -1) : csharpType;

    switch (baseType) {
        case 'Guid': {
            // SQLite stores as BLOB (Uint8Array) or TEXT (string)
            // MessagePack-CSharp expects: 36-char string "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
            if (value instanceof Uint8Array && value.length === 16) {
                // .NET Guid.ToByteArray() layout: groups 1-3 little-endian, 4-5 big-endian
                const h = (i: number) => value[i].toString(16).padStart(2, '0');
                // Group 1 (4 bytes LE → reverse for hex string)
                const g1 = h(3) + h(2) + h(1) + h(0);
                // Group 2 (2 bytes LE → reverse)
                const g2 = h(5) + h(4);
                // Group 3 (2 bytes LE → reverse)
                const g3 = h(7) + h(6);
                // Groups 4-5 (8 bytes BE → as-is)
                const g4 = h(8) + h(9);
                const g5 = h(10) + h(11) + h(12) + h(13) + h(14) + h(15);
                return `${g1}-${g2}-${g3}-${g4}-${g5}`;
            }
            // Already a string (TEXT storage)
            return String(value);
        }

        case 'DateTime': {
            // SQLite stores as TEXT (ISO 8601)
            // MessagePack-CSharp expects: Timestamp ext (-1) → pack as Date object
            // msgpackr packs Date as Timestamp ext automatically
            if (typeof value === 'string') {
                return new Date(value);
            }
            return value;
        }

        case 'DateTimeOffset': {
            // SQLite stores as TEXT (ISO 8601 with offset)
            // MessagePack-CSharp expects: array [DateTime, short(offset minutes)]
            if (typeof value === 'string') {
                const d = new Date(value);
                // Extract offset from ISO string (e.g., "+02:00" or "Z")
                const match = value.match(/([+-])(\d{2}):(\d{2})$/);
                let offsetMinutes = 0;
                if (match) {
                    offsetMinutes = (parseInt(match[2]) * 60 + parseInt(match[3])) * (match[1] === '-' ? -1 : 1);
                }
                return [d, offsetMinutes];
            }
            return value;
        }

        case 'TimeSpan': {
            // SQLite stores as TEXT (e.g., "1.02:03:04.0050000")
            // MessagePack-CSharp expects: int64 (Ticks)
            if (typeof value === 'string') {
                // Parse .NET TimeSpan string format: [d.]hh:mm:ss[.fffffff]
                const parts = value.match(/^(-?)(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?$/);
                if (parts) {
                    const sign = parts[1] === '-' ? -1 : 1;
                    const days = parseInt(parts[2] || '0');
                    const hours = parseInt(parts[3]);
                    const minutes = parseInt(parts[4]);
                    const seconds = parseInt(parts[5]);
                    const fraction = parts[6] || '0';
                    // Ticks = 10,000,000 per second
                    const ticks = sign * (
                        ((days * 24 + hours) * 3600 + minutes * 60 + seconds) * 10000000 +
                        parseInt(fraction.padEnd(7, '0').slice(0, 7))
                    );
                    return ticks;
                }
            }
            // Numeric (stored as days or ticks)
            return Number(value);
        }

        case 'Boolean':
            // SQLite stores as INTEGER (0/1)
            // MessagePack-CSharp expects: true/false
            return value === 1 || value === true;

        case 'Decimal':
            // SQLite stores as TEXT, MessagePack-CSharp expects: string
            return String(value);

        case 'Char':
            // SQLite stores as TEXT, MessagePack-CSharp expects: uint16 (char code)
            if (typeof value === 'string' && value.length >= 1) {
                return value.charCodeAt(0);
            }
            return 0;

        case 'Enum':
            // SQLite stores as INTEGER, MessagePack-CSharp expects: integer
            return Number(value);

        case 'Int16':
        case 'Int32':
        case 'Byte':
        case 'UInt16':
        case 'UInt32':
            return Number(value);

        case 'Int64':
        case 'UInt64':
            // Read as SQLITE_TEXT in bulkExport to avoid sqlite3_column_int64 boundary errors.
            // Value arrives here as BigInt (from text parse) — pass through for msgpackr int64 packing.
            if (typeof value === 'bigint') {
                return value;
            }
            return Number(value);

        case 'Double':
        case 'Single':
            return Number(value);

        case 'String':
            return String(value);

        case 'JsonArray':
            // SQLite TEXT (JSON string) → parse to array for MessagePack serialization
            if (typeof value === 'string') {
                try {
                    return JSON.parse(value);
                } catch {
                    return value;
                }
            }
            return value;

        case 'ByteArray':
            // SQLite BLOB → already Uint8Array → msgpackr packs as bin (compatible)
            return value;

        default:
            logger.warn(MODULE_NAME, `convertValueFromSqlite: unhandled type "${csharpType}", passing through`);
            return value;
    }
}

/**
 * Build SQL INSERT statement from V2 header metadata.
 * conflictStrategy: 0=plain INSERT, 1=LastWriteWins, 2=LocalWins, 3=DeltaWins
 */
function buildInsertSql(header: V2Header, conflictStrategy: number): string {
    const tableName = header[7];
    const columns = header[8];
    const pkColumn = header[9];
    const columnNames = columns.map(c => `"${c[0]}"`);
    const placeholders = columns.map(() => '?').join(', ');

    let sql = `INSERT INTO "${tableName}" (${columnNames.join(', ')}) VALUES (${placeholders})`;

    if (conflictStrategy === 0) {
        // Seed mode: plain INSERT (no conflict handling)
        return sql;
    }

    // Build SET clause for UPDATE (all columns except primary key)
    const nonPkColumns = columns.filter(c => c[0] !== pkColumn);
    const setClause = nonPkColumns
        .map(c => `"${c[0]}" = excluded."${c[0]}"`)
        .join(', ');

    switch (conflictStrategy) {
        case 1: {
            // LastWriteWins: update only if imported is newer
            const tsColumn = columns.find(c => c[0] === 'UpdatedAt');
            const tsName = tsColumn ? tsColumn[0] : 'UpdatedAt';
            sql += ` ON CONFLICT("${pkColumn}") DO UPDATE SET ${setClause} WHERE excluded."${tsName}" > "${tableName}"."${tsName}"`;
            break;
        }
        case 2:
            // LocalWins: only insert new items
            sql += ` ON CONFLICT("${pkColumn}") DO NOTHING`;
            break;
        case 3:
            // DeltaWins: always overwrite
            sql += ` ON CONFLICT("${pkColumn}") DO UPDATE SET ${setClause}`;
            break;
    }

    return sql;
}

/**
 * Core bulk insert: builds SQL from header, converts values, inserts rows in a transaction.
 * Shared by bulkImport (V2 header in payload) and bulkImportRaw (metadata in JSON).
 */
function bulkInsertRows(db: any, header: V2Header, rows: any[][], conflictStrategy: number, label: string) {
    const columns = header[8];
    const csharpTypes = columns.map(c => c[2]);
    const sqlTypes = columns.map(c => c[1]);
    const tableName = header[7];

    logger.info(MODULE_NAME, `${label}: ${rows.length} items into "${tableName}", strategy=${conflictStrategy}`);

    const sql = buildInsertSql(header, conflictStrategy);
    logger.debug(MODULE_NAME, `${label} SQL: ${sql}`);

    let rowsAffected = 0;

    db.exec("BEGIN");
    try {
        const stmt = db.prepare(sql);
        try {
            for (let i = 0; i < rows.length; i++) {
                const row = rows[i] as any[];
                const converted = row.map((val: any, idx: number) => convertValueForSqlite(val, csharpTypes[idx], sqlTypes[idx]));
                stmt.bind(converted);
                stmt.step();
                stmt.reset();
                rowsAffected++;
            }
        } finally {
            stmt.finalize();
        }
        db.exec("COMMIT");
    } catch (error) {
        try {
            db.exec("ROLLBACK");
        } catch {
            // Ignore rollback errors
        }
        logger.error(MODULE_NAME, `${label} failed:`, error);
        throw error;
    }

    logger.info(MODULE_NAME, `✓ ${label}: ${rowsAffected} rows inserted into "${tableName}"`);
    return { rowsAffected };
}

/**
 * Bulk import: unpack V2 MessagePack payload (header + individually packed rows).
 */
async function bulkImport(dbName: string, payload: Uint8Array, conflictStrategy: number) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const objects = bigIntUnpackr.unpackMultiple(payload);
    if (objects.length < 1) {
        throw new Error('bulkImport: empty payload');
    }

    const header: V2Header = objects[0];
    const rows = objects.slice(1) as any[][];

    return bulkInsertRows(db, header, rows, conflictStrategy, 'bulkImport');
}

/**
 * Bulk import from raw MessagePack row data (no V2 header).
 * Metadata comes from the JSON message, rows are a single packed array.
 */
async function bulkImportRaw(dbName: string, payload: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const { tableName, columns, primaryKeyColumn, conflictStrategy } = metadata;
    if (!tableName || !columns || !primaryKeyColumn) {
        throw new Error('bulkImportRaw requires tableName, columns, and primaryKeyColumn in metadata');
    }

    const header: V2Header = {
        0: 'SWBV2', 1: '', 2: '', 3: null, 4: '', 5: 0, 6: 0,
        7: tableName,
        8: columns,
        9: primaryKeyColumn
    };

    const rows = bigIntUnpackr.unpack(payload) as any[][];
    if (!Array.isArray(rows)) {
        throw new Error('bulkImportRaw: payload must be a MessagePack array of row arrays');
    }

    return bulkInsertRows(db, header, rows, conflictStrategy ?? 0, 'bulkImportRaw');
}

/**
 * Bulk export: query SQLite, pack V2 header + rows as MessagePack, return as raw binary.
 */
async function bulkExport(dbName: string, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const { tableName, columns, primaryKeyColumn, schemaHash, dataType,
            appIdentifier, mode, where, whereParams, orderBy } = metadata;

    if (!tableName || !columns) {
        throw new Error('bulkExport requires tableName and columns metadata');
    }

    // Build SELECT statement
    const columnNames = (columns as string[][]).map((c: string[]) => `"${c[0]}"`);
    let sql = `SELECT ${columnNames.join(', ')} FROM "${tableName}"`;

    if (where) {
        sql += ` WHERE ${where}`;
    }

    if (orderBy) {
        sql += ` ORDER BY ${orderBy}`;
    }

    logger.info(MODULE_NAME, `bulkExport: "${tableName}" — ${sql.substring(0, 120)}`);

    // Prepared statement for memory-safe row-by-row export.
    // Int64/UInt64 columns read as SQLITE_TEXT to avoid sqlite3_column_int64
    // boundary errors (returns wrong BigInt for values near int64 limits).
    const colMeta = columns as string[][];
    const csharpTypes = colMeta.map((c: string[]) => c[2]);
    const sqlTypes = colMeta.map((c: string[]) => c[1]);
    const colCount = colMeta.length;

    // Pre-compute which columns need text-based BigInt reading
    const isInt64Col = csharpTypes.map(t => {
        const base = t.endsWith('?') ? t.slice(0, -1) : t;
        return base === 'Int64' || base === 'UInt64';
    });
    const SQLITE_TEXT = sqlite3!.capi.SQLITE_TEXT;

    const rows: any[][] = [];
    const stmt = db.prepare(sql);
    try {
        if (whereParams) {
            const binds: Record<string, any> = {};
            (whereParams as any[]).forEach((v: any, i: number) => {
                binds[`$${i}`] = v;
            });
            stmt.bind(binds);
        }

        while (stmt.step()) {
            const row: any[] = [];
            for (let i = 0; i < colCount; i++) {
                if (isInt64Col[i]) {
                    // Read as text to bypass buggy sqlite3_column_int64, then parse to BigInt
                    const textVal = stmt.get(i, SQLITE_TEXT);
                    row.push(textVal !== null ? BigInt(textVal as string) : null);
                } else {
                    row.push(stmt.get(i));
                }
            }
            rows.push(row);
        }
    } finally {
        stmt.finalize();
    }

    // Build V2 header
    const header = [
        'SWBV2',           // [0] magic
        schemaHash || '',  // [1] schemaHash
        dataType || '',    // [2] dataType
        appIdentifier,     // [3] appIdentifier
        new Date().toISOString(), // [4] exportedAt
        rows.length,       // [5] recordCount
        mode ?? 0,         // [6] mode
        tableName,         // [7] tableName
        columns,           // [8] columns metadata
        primaryKeyColumn || '' // [9] primaryKeyColumn
    ];

    const parts: Uint8Array[] = [];
    parts.push(pack(header));
    for (const row of rows) {
        const converted = row.map((val, idx) =>
            convertValueFromSqlite(val, csharpTypes[idx], sqlTypes[idx]));
        parts.push(pack(converted));
    }

    // Concatenate into single buffer
    const totalLength = parts.reduce((sum, p) => sum + p.length, 0);
    const result = new Uint8Array(totalLength);
    let offset = 0;
    for (const part of parts) {
        result.set(part, offset);
        offset += part.length;
    }

    logger.info(MODULE_NAME, `✓ bulkExport: ${rows.length} rows, ${totalLength} bytes`);
    return { rawBinary: true, data: result };
}
