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

let sqlite3: any;
let poolUtil: any;
const openDatabases = new Map<string, any>();
const pragmasSet = new Set<string>(); // Track which databases have PRAGMAs configured

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
self.onmessage = async (event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number } | { type: 'init'; baseHref: string }>) => {
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
            return await openDatabase(database!);

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
                (data as any).conflictStrategy ?? 0,
                (data as any).readonlyColumns as Record<string, string[]> | undefined
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

        case 'bulkExportEncrypted':
            if (!binaryPayload) {
                throw new Error('bulkExportEncrypted requires binaryPayload (contentKey)');
            }
            return await bulkExportEncrypted(database!, new Uint8Array(binaryPayload), data as any);

        case 'bulkExportEncryptedV2':
            if (!binaryPayload) {
                throw new Error('bulkExportEncryptedV2 requires binaryPayload (V2CryptoHeader)');
            }
            return await bulkExportEncryptedV2(database!, new Uint8Array(binaryPayload), data as any);

        case 'bulkImportEncrypted':
            if (!binaryPayload || !binaryHeader) {
                throw new Error('bulkImportEncrypted requires binaryPayload (ciphertext) + binaryHeader (nonce+key)');
            }
            return await bulkImportEncrypted(
                database!,
                new Uint8Array(binaryPayload),
                new Uint8Array(binaryHeader),
                data as any
            );

        case 'bulkRotateKey':
            if (!binaryPayload) {
                throw new Error('bulkRotateKey requires binaryPayload (oldKey+newKey = 64 bytes)');
            }
            return await bulkRotateKey(database!, new Uint8Array(binaryPayload), data as any);

        default:
            throw new Error(`Unknown request type: ${type}`);
    }
}

async function openDatabase(dbName: string) {
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
function bulkInsertRows(db: any, header: V2Header, rows: any[][], conflictStrategy: number, label: string, readonlyColumnsMap?: Record<string, string[]>) {
    const columns = header[8];
    const csharpTypes = columns.map(c => c[2]);
    const sqlTypes = columns.map(c => c[1]);
    const tableName = header[7];
    const pkColumn = header[9];

    // Look up readonly columns for this specific table
    const readonlyColumns = readonlyColumnsMap?.[tableName];

    logger.info(MODULE_NAME, `${label}: ${rows.length} items into "${tableName}", strategy=${conflictStrategy}`);

    const sql = buildInsertSql(header, conflictStrategy);
    logger.debug(MODULE_NAME, `${label} SQL: ${sql}`);

    let rowsAffected = 0;

    db.exec("BEGIN");
    try {
        // Snapshot readonly columns before apply (if validation requested)
        if (readonlyColumns && readonlyColumns.length > 0) {
            const roCols = readonlyColumns.map(c => `"${c}"`).join(', ');
            db.exec(`CREATE TEMP TABLE IF NOT EXISTS _readonlySnapshot AS SELECT "${pkColumn}", ${roCols} FROM "${tableName}" WHERE 0`);
            db.exec(`DELETE FROM _readonlySnapshot`);
            db.exec(`INSERT INTO _readonlySnapshot SELECT "${pkColumn}", ${roCols} FROM "${tableName}"`);
        }

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

        // Validate readonly columns weren't mutated AND no new rows inserted
        if (readonlyColumns && readonlyColumns.length > 0) {
            // Check for new rows (not in snapshot = new inserts → rejected)
            const newRowSql = `SELECT t."${pkColumn}" FROM "${tableName}" t LEFT JOIN _readonlySnapshot s ON t."${pkColumn}" = s."${pkColumn}" WHERE s."${pkColumn}" IS NULL LIMIT 1`;
            const newRows = db.exec({ sql: newRowSql, returnValue: 'resultRows', rowMode: 'array' });
            if (newRows && newRows.length > 0) {
                db.exec(`DROP TABLE IF EXISTS _readonlySnapshot`);
                throw new Error(`Readonly column violation: sender cannot insert new rows when readonly columns are enforced`);
            }

            // Check for mutations on existing rows
            const violations: string[] = [];
            for (const col of readonlyColumns) {
                const checkSql = `SELECT s."${pkColumn}" FROM _readonlySnapshot s JOIN "${tableName}" t ON s."${pkColumn}" = t."${pkColumn}" WHERE s."${col}" IS NOT t."${col}" LIMIT 1`;
                const result = db.exec({ sql: checkSql, returnValue: 'resultRows', rowMode: 'array' });
                if (result && result.length > 0) {
                    violations.push(col);
                }
            }
            db.exec(`DROP TABLE IF EXISTS _readonlySnapshot`);

            if (violations.length > 0) {
                throw new Error(`Readonly column violation: ${violations.join(', ')} were mutated by sender`);
            }
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
async function bulkImport(dbName: string, payload: Uint8Array, conflictStrategy: number, readonlyColumns?: Record<string, string[]>) {
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

    return bulkInsertRows(db, header, rows, conflictStrategy, 'bulkImport', readonlyColumns);
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

// ============================================================
// ENCRYPTED BULK OPERATIONS (SubtleCrypto AES-GCM, content key zeroed after use)
// ============================================================

function base64ToBytes(base64: string): Uint8Array {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

/**
 * Encrypted bulk export:
 *   1. Read the open table → V2 bytes (header + rows as MessagePack).
 *   2. Parse V2 → (header, rows).
 *   3. Populate the sender's `_crypto_<table>` shadow with one entry per row, each
 *      individually encrypted with a fresh nonce under the content key. This keeps
 *      the sender's shadow in sync with their open table so BulkRotateKeyAsync has
 *      something to rotate on the sender side, and so recovery from a local wipe
 *      can restore the open table from shadow ciphertext.
 *   4. Encrypt the whole V2 blob with a single nonce under the same content key —
 *      this is the wire envelope shipped to recipients.
 *
 * Symmetric with bulkImportEncrypted: both sides populate the per-row shadow.
 */
async function bulkExportEncrypted(dbName: string, contentKeyPayload: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    // 1. Normal export → V2 bytes
    const exportResult = await bulkExport(dbName, metadata);
    const v2Bytes = (exportResult as any).data as Uint8Array;

    if (!(v2Bytes instanceof Uint8Array)) {
        throw new Error(`bulkExportEncrypted: expected Uint8Array from bulkExport, got ${typeof v2Bytes}`);
    }

    // 2. Content key from binary payload (32 bytes). Copy into a fresh
    //    ArrayBuffer-backed Uint8Array so TS sees a narrow type and we can
    //    zero it unconditionally in finally. Import once, reuse for envelope
    //    encrypt + per-row shadow encrypt.
    const contentKeyBytes = new Uint8Array(32);
    contentKeyBytes.set(contentKeyPayload.subarray(0, 32));
    let contentKey: CryptoKey;
    try {
        contentKey = await crypto.subtle.importKey(
            'raw', contentKeyBytes, { name: 'AES-GCM' }, false, ['encrypt']);
    } finally {
        contentKeyBytes.fill(0);
    }

    // 3. Populate sender's shadow per-row, if the crypto shadow table exists for this table.
    //    Skipped for non-syncable entities (no _crypto_ table, no Id/SharingScope/SharingId).
    const objects = bigIntUnpackr.unpackMultiple(v2Bytes);
    if (objects.length >= 1) {
        const header = objects[0] as any;
        const tableName = header[7] as string;
        const rows = objects.slice(1) as any[][];
        const cryptoTableName = `_crypto_${tableName}`;

        if (rows.length > 0) {
            const tableCheck = db.exec({
                sql: `SELECT name FROM sqlite_master WHERE type='table' AND name=?`,
                bind: [cryptoTableName],
                returnValue: 'resultRows',
                rowMode: 'array'
            });

            if (tableCheck && tableCheck.length > 0) {
                const columns = header[8] as any[];
                const columnNames = columns.map((c: any[]) => c[0] as string);
                const idIdx = columnNames.indexOf('Id');
                const scopeIdx = columnNames.indexOf('SharingScope');
                const sharingIdIdx = columnNames.indexOf('SharingId');

                if (idIdx < 0 || scopeIdx < 0 || sharingIdIdx < 0) {
                    logger.warn(MODULE_NAME,
                        `bulkExportEncrypted: ${tableName} is not a SyncableEntity (missing Id/SharingScope/SharingId); skipping shadow population`);
                } else {
                    const shadowSql =
                        `INSERT OR REPLACE INTO "${cryptoTableName}" (Id, SharingScope, SharingId, EncryptedRow, Nonce) ` +
                        `VALUES (?, ?, ?, ?, ?)`;
                    const stmt = db.prepare(shadowSql);

                    db.exec('BEGIN');
                    let shadowCount = 0;
                    try {
                        for (let i = 0; i < rows.length; i++) {
                            const row = rows[i];
                            const rowBytes = pack(row);
                            const rowNonce = crypto.getRandomValues(new Uint8Array(12));
                            const rowCipher = await crypto.subtle.encrypt(
                                { name: 'AES-GCM', iv: rowNonce.buffer as ArrayBuffer },
                                contentKey,
                                rowBytes.buffer.slice(rowBytes.byteOffset, rowBytes.byteOffset + rowBytes.byteLength) as ArrayBuffer);

                            stmt.bind([
                                row[idIdx],
                                row[scopeIdx],
                                row[sharingIdIdx],
                                new Uint8Array(rowCipher),
                                rowNonce
                            ]);
                            stmt.step();
                            stmt.reset();
                            shadowCount++;
                        }
                        stmt.finalize();
                        db.exec('COMMIT');
                        logger.info(MODULE_NAME, `✓ bulkExportEncrypted: sender shadow populated with ${shadowCount} rows in ${cryptoTableName}`);
                    } catch (e) {
                        try { stmt.finalize(); } catch { /* ignore */ }
                        try { db.exec('ROLLBACK'); } catch { /* ignore */ }
                        logger.error(MODULE_NAME, `bulkExportEncrypted: sender shadow population failed in ${cryptoTableName}:`, e);
                        throw e;
                    }
                }
            }
        }
    }

    // 4. Encrypt the whole V2 blob with a single nonce → the wire envelope.
    const nonce = crypto.getRandomValues(new Uint8Array(12));
    const ciphertext = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv: nonce.buffer as ArrayBuffer },
        contentKey,
        v2Bytes.buffer.slice(v2Bytes.byteOffset, v2Bytes.byteOffset + v2Bytes.byteLength) as ArrayBuffer);

    // Return [12-byte nonce | ciphertext] as single binary blob
    const result = new Uint8Array(12 + ciphertext.byteLength);
    result.set(nonce, 0);
    result.set(new Uint8Array(ciphertext), 12);

    logger.info(MODULE_NAME, `✓ bulkExportEncrypted: ${v2Bytes.length} → ${result.length} bytes (envelope)`);
    return { rawBinary: true, data: result };
}

// ============================================================================
// V2 encrypted export — shadow rows ARE the wire format (no outer envelope).
// Stage 5 / D-3 rework. Derived single-actor content keys via HKDF-SHA256 from
// the session's X25519 private key. ECIES unwrap for two-actor (non-owner
// recipient) scenarios is a later stage — rows whose scope this device does
// not own are left for Stage 9.
// ============================================================================

// Info strings MUST byte-match SqliteWasmBlazor.CryptoSync.KeyDerivation.
// System scope and domain scope are intentionally distinct strings so the
// same private key produces cryptographically independent content keys.
const SYSTEM_CONTENT_KEY_INFO = 'SqliteWasmBlazor.CryptoSync.SystemContentKey.v1';
const DOMAIN_CONTENT_KEY_INFO_PREFIX = 'SqliteWasmBlazor.CryptoSync.ContentKey.v1:';

// Sharing scope discriminator — MUST match SqliteWasmBlazor.CryptoSync.SharingScope.
// Public/Shared/Client are the three buckets; only Public+SharingId="system" is
// treated as the admin "system" scope (deriving via SystemContentKey info).
const SHARING_SCOPE_PUBLIC = 0;
const SYSTEM_SHARING_ID = 'system';

/**
 * Derive a 32-byte AES-GCM content key from an X25519 private key via
 * HKDF-SHA256. Byte-compatible with .NET's HKDF.DeriveKey per RFC 5869:
 * salt is empty (WebCrypto substitutes 32 zero bytes internally), info
 * is the same UTF-8 string the C# side passes.
 *
 * Returned Uint8Array aliases an ArrayBuffer — caller is responsible for
 * zeroing via .fill(0) once the derived AES key has been imported.
 */
async function deriveHkdfContentKey(privateKey: Uint8Array, info: string): Promise<Uint8Array> {
    const ikm = await crypto.subtle.importKey(
        'raw',
        privateKey.buffer.slice(privateKey.byteOffset, privateKey.byteOffset + privateKey.byteLength) as ArrayBuffer,
        { name: 'HKDF' },
        false,
        ['deriveBits']);

    const bits = await crypto.subtle.deriveBits(
        {
            name: 'HKDF',
            hash: 'SHA-256',
            salt: new Uint8Array(0),
            info: new TextEncoder().encode(info)
        },
        ikm,
        256); // 256 bits = 32 bytes

    return new Uint8Array(bits);
}

/**
 * Parse a MessagePack-serialized V2CryptoHeader from the C# side. The C#
 * record uses [MessagePackObject] with [Key(N)] attributes, which serialize
 * as a MessagePack array in Key order:
 *   [0] Version (int)
 *   [1] SystemTables (string[])
 *   [2] SharingTableName (string)
 *   [3] ClientContactId (Guid — MessagePack-CSharp default emits as 16 LE bytes)
 *   [4] ClientPrivateKey (byte[])
 */
interface V2CryptoHeader {
    version: number;
    systemTables: string[];
    sharingTableName: string;
    clientContactIdBytes: Uint8Array;   // 16 bytes, raw Guid payload
    clientPrivateKey: Uint8Array;       // 32 bytes X25519 private key
}

function parseV2CryptoHeader(bytes: Uint8Array): V2CryptoHeader {
    const arr = unpack(bytes) as unknown;
    if (!Array.isArray(arr) || arr.length < 5) {
        throw new Error(`bulkExportEncryptedV2: V2CryptoHeader expected 5-element array, got ${JSON.stringify(arr)}`);
    }

    const version = arr[0] as number;
    const systemTables = arr[1] as string[];
    const sharingTableName = arr[2] as string;
    const contactId = arr[3];
    const privateKey = arr[4];

    if (typeof version !== 'number') {
        throw new Error(`V2CryptoHeader: Version must be int, got ${typeof version}`);
    }
    if (!Array.isArray(systemTables)) {
        throw new Error('V2CryptoHeader: SystemTables must be array');
    }
    if (typeof sharingTableName !== 'string') {
        throw new Error('V2CryptoHeader: SharingTableName must be string');
    }
    if (!(contactId instanceof Uint8Array) || contactId.byteLength !== 16) {
        throw new Error(`V2CryptoHeader: ClientContactId must be 16-byte blob, got ${contactId}`);
    }
    if (!(privateKey instanceof Uint8Array) || privateKey.byteLength !== 32) {
        throw new Error(`V2CryptoHeader: ClientPrivateKey must be 32-byte blob, got ${privateKey?.byteLength ?? 'undefined'}`);
    }

    return {
        version,
        systemTables,
        sharingTableName,
        clientContactIdBytes: contactId,
        clientPrivateKey: privateKey
    };
}

/**
 * V2 encrypted bulk export — shadow rows ARE the wire format.
 *
 * Flow:
 *   1. Parse the V2CryptoHeader (binary payload).
 *   2. Call the existing bulkExport → V2 MessagePack bytes (header + rows).
 *   3. Walk rows, group by (SharingScope, SharingId), derive one HKDF
 *      content key per distinct group, cache it for reuse, encrypt every
 *      row under its group's key with a fresh 12-byte nonce.
 *   4. Upsert each row into the sender's `_crypto_<table>` shadow.
 *   5. Build a ShadowRowGroup and return it MessagePack-packed as the wire
 *      payload. The orchestrator (C# side) bundles multiple groups into a
 *      DeltaEnvelope.
 *
 * Single-actor scope only — rows for scopes this device does not own are
 * currently treated as ownable (the private key derivation still runs, but
 * the receiver will reject if byte-compat fails). Two-actor ECIES unwrap
 * lands in a later stage.
 */
async function bulkExportEncryptedV2(dbName: string, headerBytes: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const header = parseV2CryptoHeader(headerBytes);

    // Copy the private key into a fresh buffer so the caller's copy can be
    // zeroed independently, and so our .fill(0) in finally unconditionally
    // clears OUR copy regardless of how the caller manages theirs.
    const privateKeyCopy = new Uint8Array(32);
    privateKeyCopy.set(header.clientPrivateKey);

    try {
        // Step 1: normal export → V2 bytes (header + rows as MessagePack)
        const exportResult = await bulkExport(dbName, metadata);
        const v2Bytes = (exportResult as any).data as Uint8Array;
        if (!(v2Bytes instanceof Uint8Array)) {
            throw new Error(`bulkExportEncryptedV2: expected Uint8Array from bulkExport, got ${typeof v2Bytes}`);
        }

        const objects = bigIntUnpackr.unpackMultiple(v2Bytes);
        if (objects.length < 1) {
            throw new Error('bulkExportEncryptedV2: empty v2 payload');
        }

        const v2Header = objects[0] as any;
        const tableName = v2Header[7] as string;
        const rows = objects.slice(1) as any[][];
        const cryptoTableName = `_crypto_${tableName}`;

        // Resolve SyncableEntity column indices — rows without these cannot be
        // shadowed and will be rejected. Matches the existing bulkExportEncrypted
        // precondition.
        const columns = v2Header[8] as any[];
        const columnNames = columns.map((c: any[]) => c[0] as string);
        const idIdx = columnNames.indexOf('Id');
        const scopeIdx = columnNames.indexOf('SharingScope');
        const sharingIdIdx = columnNames.indexOf('SharingId');
        if (idIdx < 0 || scopeIdx < 0 || sharingIdIdx < 0) {
            throw new Error(
                `bulkExportEncryptedV2: ${tableName} is not a SyncableEntity (missing Id/SharingScope/SharingId)`);
        }

        // Verify the shadow table exists — if not, nothing to upsert into and
        // the whole call is a config error worth surfacing loudly.
        const tableCheck = db.exec({
            sql: `SELECT name FROM sqlite_master WHERE type='table' AND name=?`,
            bind: [cryptoTableName],
            returnValue: 'resultRows',
            rowMode: 'array'
        });
        if (!tableCheck || tableCheck.length === 0) {
            throw new Error(`bulkExportEncryptedV2: shadow table ${cryptoTableName} not found`);
        }

        const isSystemTable = header.systemTables.indexOf(tableName) >= 0;

        // Group (scope, sharingId) → AES-GCM CryptoKey. Cache so each distinct
        // group pays the HKDF cost once per call.
        const keyCache = new Map<string, CryptoKey>();

        async function getOrDeriveKey(scope: number, sharingId: string): Promise<CryptoKey> {
            const cacheKey = `${scope}:${sharingId}`;
            const cached = keyCache.get(cacheKey);
            if (cached) {
                return cached;
            }

            // Info-string rule matches KeyDerivation on the C# side:
            // - Public + "system"  → SystemContentKey info
            // - anything else      → DomainContentKey info + ":" + sharingId
            const info = (scope === SHARING_SCOPE_PUBLIC && sharingId === SYSTEM_SHARING_ID)
                ? SYSTEM_CONTENT_KEY_INFO
                : DOMAIN_CONTENT_KEY_INFO_PREFIX + sharingId;

            const rawKey = await deriveHkdfContentKey(privateKeyCopy, info);
            try {
                const cryptoKey = await crypto.subtle.importKey(
                    'raw',
                    rawKey.buffer.slice(rawKey.byteOffset, rawKey.byteOffset + rawKey.byteLength) as ArrayBuffer,
                    { name: 'AES-GCM' },
                    false,
                    ['encrypt']);
                keyCache.set(cacheKey, cryptoKey);
                return cryptoKey;
            } finally {
                rawKey.fill(0);
            }
        }

        // Build the ShadowRowGroup payload and upsert the sender shadow inside
        // a single transaction. The shadow upsert uses the same per-row layout
        // the old path produced, so the rotation path (BulkRotateKeyAsync) keeps
        // working unchanged.
        const shadowSql =
            `INSERT OR REPLACE INTO "${cryptoTableName}" (Id, SharingScope, SharingId, EncryptedRow, Nonce) ` +
            `VALUES (?, ?, ?, ?, ?)`;
        const stmt = db.prepare(shadowSql);

        // ShadowRowGroup / ShadowRow array layouts — must match
        // SqliteWasmBlazor.CryptoSync.ShadowRow / ShadowRowGroup [Key(N)]:
        //   ShadowRow:       [Id(Guid 16 bytes), SharingScope(int), SharingId(str), EncryptedRow(bytes), Nonce(bytes)]
        //   ShadowRowGroup:  [TableName(str), IsSystemTable(bool), Rows(ShadowRow[])]
        const shadowRowArrays: unknown[][] = [];

        db.exec('BEGIN');
        try {
            for (let i = 0; i < rows.length; i++) {
                const row = rows[i];
                const rowScope = Number(row[scopeIdx]);
                const rowSharingId = String(row[sharingIdIdx]);
                const rowIdBytes = guidToBytes(row[idIdx]);

                const key = await getOrDeriveKey(rowScope, rowSharingId);

                const rowBytes = pack(row);
                const rowNonce = crypto.getRandomValues(new Uint8Array(12));
                const rowCipherBuf = await crypto.subtle.encrypt(
                    { name: 'AES-GCM', iv: rowNonce.buffer as ArrayBuffer },
                    key,
                    rowBytes.buffer.slice(rowBytes.byteOffset, rowBytes.byteOffset + rowBytes.byteLength) as ArrayBuffer);
                const rowCipher = new Uint8Array(rowCipherBuf);

                stmt.bind([
                    row[idIdx],
                    rowScope,
                    rowSharingId,
                    rowCipher,
                    rowNonce
                ]);
                stmt.step();
                stmt.reset();

                shadowRowArrays.push([
                    rowIdBytes,
                    rowScope,
                    rowSharingId,
                    rowCipher,
                    rowNonce
                ]);
            }
            stmt.finalize();
            db.exec('COMMIT');
        } catch (e) {
            try { stmt.finalize(); } catch { /* ignore */ }
            try { db.exec('ROLLBACK'); } catch { /* ignore */ }
            logger.error(MODULE_NAME, `bulkExportEncryptedV2: shadow upsert failed in ${cryptoTableName}:`, e);
            throw e;
        }

        const groupArray: unknown[] = [tableName, isSystemTable, shadowRowArrays];
        const packed = pack(groupArray);

        logger.info(MODULE_NAME,
            `✓ bulkExportEncryptedV2: ${tableName} → ${shadowRowArrays.length} rows, ${packed.length} bytes (wire=shadow)`);
        return { rawBinary: true, data: packed };
    } finally {
        privateKeyCopy.fill(0);
    }
}

/**
 * Convert a Guid from whatever shape bulkExport produced (string or already
 * a Uint8Array) into the 16-byte payload the C# MessagePack Guid formatter
 * produces. MessagePack-CSharp's default Guid formatter writes raw
 * little-endian 16 bytes; msgpackr decodes BinData as Uint8Array directly.
 *
 * Callers that already passed a Uint8Array get it back unchanged. String
 * inputs are parsed via the standard 8-4-4-4-12 hex layout, with the first
 * three groups byte-reversed to match .NET's in-memory Guid layout.
 */
function guidToBytes(value: unknown): Uint8Array {
    if (value instanceof Uint8Array) {
        if (value.byteLength !== 16) {
            throw new Error(`guidToBytes: Uint8Array must be 16 bytes, got ${value.byteLength}`);
        }
        return value;
    }
    if (typeof value === 'string') {
        const hex = value.replace(/-/g, '');
        if (hex.length !== 32) {
            throw new Error(`guidToBytes: string must be 32 hex chars, got ${hex.length}`);
        }
        const bytes = new Uint8Array(16);
        for (let i = 0; i < 16; i++) {
            bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
        }
        // .NET Guid in-memory layout: first 4 bytes LE, next 2 bytes LE,
        // next 2 bytes LE, then 8 bytes BE as-is. Reverse the first three
        // groups.
        const swap = (a: number, b: number) => { const t = bytes[a]; bytes[a] = bytes[b]; bytes[b] = t; };
        swap(0, 3); swap(1, 2);
        swap(4, 5);
        swap(6, 7);
        return bytes;
    }
    throw new Error(`guidToBytes: unsupported Guid shape ${typeof value}`);
}

/**
 * Encrypted bulk import:
 *   1. Decrypt the envelope with the content key.
 *   2. Parse V2 → (header, rows).
 *   3. Populate the `_crypto_<table>` shadow with one entry per row, each individually
 *      encrypted with a fresh nonce under the same content key. This is the recovery /
 *      rotation source — subsequent BulkRotateKeyAsync walks this table row-by-row.
 *   4. Insert the plaintext rows into the open table via bulkInsertRows.
 *
 * Content key bytes are zeroed after importKey. The CryptoKey handle survives and is
 * reused for both the envelope decrypt and the per-row shadow encrypts.
 */
async function bulkImportEncrypted(dbName: string, encryptedPayload: Uint8Array, cryptoHeader: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    // cryptoHeader: [nonce(12) | contentKey(32)]
    // Copy into fresh ArrayBuffer-backed Uint8Arrays for strict TS typing and
    // unconditional zeroing.
    const nonceBytes = new Uint8Array(12);
    nonceBytes.set(cryptoHeader.subarray(0, 12));
    const contentKeyBytes = new Uint8Array(32);
    contentKeyBytes.set(cryptoHeader.subarray(12, 44));

    // Import the key with BOTH capabilities once, reuse for envelope decrypt + per-row encrypt.
    // Once imported, the source bytes can be zeroed — CryptoKey retains the material internally.
    let contentKey: CryptoKey;
    try {
        contentKey = await crypto.subtle.importKey(
            'raw', contentKeyBytes, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt']);
    } finally {
        contentKeyBytes.fill(0);
    }

    // Copy the encrypted payload into a fresh ArrayBuffer-backed Uint8Array for
    // the same TS strictness reason.
    const encryptedPayloadCopy = new Uint8Array(encryptedPayload.byteLength);
    encryptedPayloadCopy.set(encryptedPayload);

    let v2Bytes: Uint8Array;
    try {
        const plaintext = await crypto.subtle.decrypt(
            { name: 'AES-GCM', iv: nonceBytes }, contentKey, encryptedPayloadCopy);
        v2Bytes = new Uint8Array(plaintext);
    } finally {
        nonceBytes.fill(0);
    }

    // Parse V2 header to get table name, column layout, and rows
    const objects = bigIntUnpackr.unpackMultiple(v2Bytes);
    if (objects.length < 1) {
        throw new Error('bulkImportEncrypted: empty decrypted payload');
    }

    const header = objects[0] as any;
    const tableName = header[7] as string;
    const rows = objects.slice(1) as any[][];

    // Populate the shadow table with one per-row entry.
    // SyncableEntity-backed tables have Id/SharingScope/SharingId columns; non-syncable
    // tables don't and are skipped here (their _crypto_ shadow doesn't exist anyway).
    const cryptoTableName = `_crypto_${tableName}`;
    const tableCheck = db.exec({
        sql: `SELECT name FROM sqlite_master WHERE type='table' AND name=?`,
        bind: [cryptoTableName],
        returnValue: 'resultRows',
        rowMode: 'array'
    });

    if (tableCheck && tableCheck.length > 0 && rows.length > 0) {
        const columns = header[8] as any[]; // [[name, sqlType, csharpType], ...]
        const columnNames = columns.map((c: any[]) => c[0] as string);
        const idIdx = columnNames.indexOf('Id');
        const scopeIdx = columnNames.indexOf('SharingScope');
        const sharingIdIdx = columnNames.indexOf('SharingId');

        if (idIdx < 0 || scopeIdx < 0 || sharingIdIdx < 0) {
            logger.warn(MODULE_NAME,
                `bulkImportEncrypted: ${tableName} is not a SyncableEntity (missing Id/SharingScope/SharingId); skipping shadow population`);
        } else {
            const shadowSql =
                `INSERT OR REPLACE INTO "${cryptoTableName}" (Id, SharingScope, SharingId, EncryptedRow, Nonce) ` +
                `VALUES (?, ?, ?, ?, ?)`;
            const stmt = db.prepare(shadowSql);

            db.exec('BEGIN');
            let shadowCount = 0;
            try {
                for (let i = 0; i < rows.length; i++) {
                    const row = rows[i];
                    // Serialize THIS row (as an array) to MessagePack.
                    const rowBytes = pack(row);

                    // Fresh per-row nonce.
                    const rowNonce = crypto.getRandomValues(new Uint8Array(12));

                    // Encrypt with the content key already imported above.
                    const rowCipher = await crypto.subtle.encrypt(
                        { name: 'AES-GCM', iv: rowNonce.buffer as ArrayBuffer },
                        contentKey,
                        rowBytes.buffer.slice(rowBytes.byteOffset, rowBytes.byteOffset + rowBytes.byteLength) as ArrayBuffer);

                    stmt.bind([
                        row[idIdx],
                        row[scopeIdx],
                        row[sharingIdIdx],
                        new Uint8Array(rowCipher),
                        rowNonce
                    ]);
                    stmt.step();
                    stmt.reset();
                    shadowCount++;
                }
                stmt.finalize();
                db.exec('COMMIT');
                logger.info(MODULE_NAME, `✓ Shadow populated: ${shadowCount} rows in ${cryptoTableName}`);
            } catch (e) {
                try { stmt.finalize(); } catch { /* ignore */ }
                try { db.exec('ROLLBACK'); } catch { /* ignore */ }
                logger.error(MODULE_NAME, `Shadow population failed in ${cryptoTableName}:`, e);
                throw e;
            }
        }
    }

    // Import decrypted rows into open table
    const conflictStrategy = metadata.conflictStrategy ?? 0;
    const readonlyColumns = metadata.readonlyColumns as Record<string, string[]> | undefined;

    logger.info(MODULE_NAME, `✓ bulkImportEncrypted: decrypted ${v2Bytes.length} bytes, ${rows.length} rows`);
    return bulkInsertRows(db, header, rows, conflictStrategy, 'bulkImportEncrypted', readonlyColumns);
}

/**
 * Bulk re-key rotation: re-encrypts every row in a crypto shadow table under a new content key,
 * in place, inside a single SQLite transaction. Executes entirely in the worker — plain and
 * ciphertext bytes never leave this function.
 *
 * This is the hot path for revoke and ownership-transfer operations. No C# round-trip of data —
 * C# only hands over the two keys (64 bytes total) and a filter, and receives a row count.
 *
 * Payload layout: 64 bytes = oldKey[0..32] | newKey[32..64]
 * Metadata: { type: "bulkRotateKey", database, tableName, sharingId? }
 *   - tableName: the domain table ("CryptoTestItems", "ShoppingItems", …). The worker operates
 *     on the corresponding "_crypto_<tableName>" shadow table.
 *   - sharingId: optional filter. When provided, only shadow rows where SharingId = this value
 *     are rotated (scopes the revoke to one ShareGroup). When omitted, every row in the shadow
 *     is rotated.
 *
 * Returns: { rowsAffected }
 */
async function bulkRotateKey(dbName: string, keyPayload: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const tableName = metadata.tableName as string | undefined;
    if (!tableName) {
        throw new Error('bulkRotateKey: metadata.tableName is required');
    }

    if (keyPayload.length < 64) {
        throw new Error(`bulkRotateKey: keyPayload must be 64 bytes (oldKey+newKey), got ${keyPayload.length}`);
    }

    const sharingId = metadata.sharingId as string | undefined;
    const cryptoTable = `_crypto_${tableName}`;

    // Copy key material into local buffers so we can zero them unconditionally in finally.
    const oldKeyBytes = new Uint8Array(32);
    const newKeyBytes = new Uint8Array(32);
    oldKeyBytes.set(keyPayload.slice(0, 32));
    newKeyBytes.set(keyPayload.slice(32, 64));

    try {
        // Verify the shadow table exists before starting any work.
        const tableCheck = db.exec({
            sql: `SELECT name FROM sqlite_master WHERE type='table' AND name=?`,
            bind: [cryptoTable],
            returnValue: 'resultRows',
            rowMode: 'array'
        });
        if (!tableCheck || tableCheck.length === 0) {
            throw new Error(`bulkRotateKey: crypto shadow table not found: ${cryptoTable}`);
        }

        // Import both keys via SubtleCrypto. Only the capabilities we need.
        const oldKey = await crypto.subtle.importKey(
            'raw', oldKeyBytes.buffer, { name: 'AES-GCM' }, false, ['decrypt']);
        const newKey = await crypto.subtle.importKey(
            'raw', newKeyBytes.buffer, { name: 'AES-GCM' }, false, ['encrypt']);

        // Read all rows that need rotation. For a real revoke this is scoped by SharingId;
        // when sharingId is omitted, we process the whole shadow (benchmark path).
        const selectSql = sharingId !== undefined
            ? `SELECT Id, EncryptedRow, Nonce FROM "${cryptoTable}" WHERE SharingId = ?`
            : `SELECT Id, EncryptedRow, Nonce FROM "${cryptoTable}"`;

        const rows = db.exec({
            sql: selectSql,
            bind: sharingId !== undefined ? [sharingId] : [],
            returnValue: 'resultRows',
            rowMode: 'array'
        });

        if (!rows || rows.length === 0) {
            logger.info(MODULE_NAME, `bulkRotateKey: no rows match in ${cryptoTable}${sharingId !== undefined ? ` for SharingId=${sharingId}` : ''}`);
            return { rowsAffected: 0 };
        }

        // Single prepared UPDATE, executed once per row inside the transaction.
        const updateSql = `UPDATE "${cryptoTable}" SET EncryptedRow = ?, Nonce = ? WHERE Id = ?`;
        const stmt = db.prepare(updateSql);

        let rowsAffected = 0;
        db.exec('BEGIN');
        try {
            for (let i = 0; i < rows.length; i++) {
                const row = rows[i] as any[];
                const id = row[0];
                const oldCipher = row[1] as Uint8Array;
                const oldNonce = row[2] as Uint8Array;

                // Decrypt with the old key
                const plaintext = await crypto.subtle.decrypt(
                    { name: 'AES-GCM', iv: oldNonce.buffer as ArrayBuffer },
                    oldKey,
                    oldCipher.buffer as ArrayBuffer
                );

                // Fresh per-row nonce, then encrypt under the new key
                const newNonce = crypto.getRandomValues(new Uint8Array(12));
                const newCipher = await crypto.subtle.encrypt(
                    { name: 'AES-GCM', iv: newNonce.buffer as ArrayBuffer },
                    newKey,
                    plaintext
                );

                stmt.bind([new Uint8Array(newCipher), newNonce, id]);
                stmt.step();
                stmt.reset();
                rowsAffected++;
            }
            stmt.finalize();
            db.exec('COMMIT');
        } catch (e) {
            try { stmt.finalize(); } catch { /* ignore */ }
            try { db.exec('ROLLBACK'); } catch { /* ignore */ }
            throw e;
        }

        logger.info(MODULE_NAME, `✓ bulkRotateKey: re-encrypted ${rowsAffected} rows in ${cryptoTable}${sharingId !== undefined ? ` (SharingId=${sharingId})` : ''}`);
        return { rowsAffected };
    } finally {
        // Zero key material we copied into this function — regardless of success/failure.
        oldKeyBytes.fill(0);
        newKeyBytes.fill(0);
    }
}
