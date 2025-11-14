// sqlite-worker.ts
// Web Worker for executing SQL with sqlite-wasm + OPFS SAHPool VFS
// SAHPool provides synchronous OPFS access in worker context

import sqlite3InitModule from '@sqlite.org/sqlite-wasm';
import { logger } from './sqlite-logger';
import { pack } from 'msgpackr';
import { registerEFCoreFunctions } from './ef-core-functions';

interface WorkerRequest {
    id: number;
    data: {
        type: string;
        database?: string;
        sql?: string;
        parameters?: Record<string, any>;
    };
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

        sqlite3 = await sqlite3InitModule({
            print: console.log,
            printErr: console.error,
            locateFile(path: string) {
                // Tell sqlite-wasm where to find the wasm file using base href
                if (path.endsWith('.wasm')) {
                    return `${baseHref}_content/SqliteWasmBlazor/${path}`;
                }
                return path;
            }
        });

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
            initialCapacity: 6,
            directory: '/databases',
            name: 'opfs-sahpool',
            clearOnInit: false
        });

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
    const { id, data } = event.data as WorkerRequest;

    try {
        const result = await handleRequest(data);

        // Check if result is MessagePack binary (Uint8Array)
        if (result instanceof Uint8Array) {
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

async function handleRequest(data: WorkerRequest['data']) {
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
            logger.info(MODULE_NAME, `Opened database: ${dbName} with OPFS SAHPool`);
        } catch (error) {
            console.error(`[SQLite Worker] Failed to open database ${dbName}:`, error);
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
function getTableSchema(db: any, tableName: string): Map<string, string> {
    if (schemaCache.has(tableName)) {
        return schemaCache.get(tableName)!;
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

        schemaCache.set(tableName, schema);
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

async function executeSql(dbName: string, sql: string, parameters: Record<string, any>) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    try {
        logger.debug(MODULE_NAME, 'Executing SQL:', sql.substring(0, 100));

        // Execute SQL - use returnValue to get the result
        const result = db.exec({
            sql: sql,
            bind: Object.keys(parameters).length > 0 ? parameters : undefined,
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
                        tableSchema = getTableSchema(db, tableName);
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

            // DEBUG: Log UPDATE operations specifically
            if (sql.trim().toUpperCase().startsWith('UPDATE')) {
                console.log(`[SQLite Worker] UPDATE executed:`, sql);
                console.log(`[SQLite Worker] Parameters:`, parameters);
                console.log(`[SQLite Worker] Has RETURNING:`, hasReturning);
                console.log(`[SQLite Worker] Result rows:`, result?.length || 0);
                console.log(`[SQLite Worker] Result values:`, result);
                console.log(`[SQLite Worker] db.changes():`, db.changes());
                console.log(`[SQLite Worker] Final rowsAffected:`, rowsAffected);
            }
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
        console.error(`[SQLite Worker] SQL execution failed:`, error);
        console.error(`SQL: ${sql}`);
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
        console.error(`[SQLite Worker] Failed to check database ${dbName}:`, error);
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

        // Use the pool utility's wipeFiles method to delete the database
        if (poolUtil.wipeFiles) {
            await poolUtil.wipeFiles(dbPath);
            console.debug(`[SQLite Worker] Deleted database: ${dbName}`);
        } else {
            console.warn(`[SQLite Worker] wipeFiles not available, database may persist`);
        }

        return { success: true };
    } catch (error) {
        console.error(`[SQLite Worker] Failed to delete database ${dbName}:`, error);
        throw error;
    }
}
