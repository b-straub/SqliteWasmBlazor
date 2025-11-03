// sqlite-worker.ts
// Web Worker for executing SQL with sqlite-wasm + OPFS SAHPool VFS
// SAHPool provides synchronous OPFS access in worker context

import sqlite3InitModule from '@sqlite.org/sqlite-wasm';
import { logger } from './sqlite-logger';

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
        rows?: any[][];
        rowsAffected?: number;
        lastInsertId?: number;
    };
}

let sqlite3: any;
let poolUtil: any;
const openDatabases = new Map<string, any>();

// Cache table schemas: Map<tableName, Map<columnName, columnType>>
const schemaCache = new Map<string, Map<string, string>>();

const MODULE_NAME = 'SQLite Worker';

// Helper function to convert BigInt and Uint8Array for JSON serialization
// BigInts within safe integer range (±2^53-1) are converted to number for efficiency
// Larger BigInts are converted to string to preserve precision
// Uint8Arrays are converted to Base64 strings (matches .NET 6+ JSInterop convention)
function convertBigIntToString(value: any): any {
    if (typeof value === 'bigint') {
        // Check if BigInt fits in JavaScript's safe integer range
        if (value >= Number.MIN_SAFE_INTEGER && value <= Number.MAX_SAFE_INTEGER) {
            return Number(value);  // Convert to number for efficiency
        }
        return value.toString();  // Convert to string to preserve precision
    }
    if (value instanceof Uint8Array) {
        // Convert Uint8Array to Base64 string (matches .NET 6+ convention)
        // Use btoa() with binary string conversion
        let binaryString = '';
        for (let i = 0; i < value.length; i++) {
            binaryString += String.fromCharCode(value[i]);
        }
        return btoa(binaryString);
    }
    if (Array.isArray(value)) {
        return value.map(convertBigIntToString);
    }
    if (value && typeof value === 'object') {
        const converted: any = {};
        for (const key in value) {
            converted[key] = convertBigIntToString(value[key]);
        }
        return converted;
    }
    return value;
}

// Initialize sqlite-wasm with OPFS SAHPool
(async () => {
    try {
        logger.info(MODULE_NAME, 'Initializing sqlite-wasm with OPFS SAHPool...');

        sqlite3 = await sqlite3InitModule({
            print: console.log,
            printErr: (msg: string) => {
                // Filter out misleading OPFS warning - we use SAHPool which doesn't need COOP/COEP
                if (msg.includes('Cannot install OPFS') ||
                    msg.includes('Missing SharedArrayBuffer') ||
                    msg.includes('COOP/COEP')) {
                    // Suppress warning about standard OPFS - we use SAHPool instead
                    return;
                }
                console.error(msg);
            },
            locateFile(path: string) {
                // Tell sqlite-wasm where to find the wasm file
                if (path.endsWith('.wasm')) {
                    return `/_content/System.Data.SQLite.Wasm/${path}`;
                }
                return path;
            }
        });

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
})();

// Handle messages from main thread
self.onmessage = async (event: MessageEvent<WorkerRequest | { type: 'setLogLevel'; level: number }>) => {
    // Handle log level changes (no response needed)
    if ('type' in event.data && event.data.type === 'setLogLevel' && 'level' in event.data) {
        logger.setLogLevel(event.data.level);
        return;
    }

    // Handle regular requests
    const { id, data } = event.data as WorkerRequest;

    try {
        const result = await handleRequest(data);

        const response: WorkerResponse = {
            id,
            data: {
                success: true,
                ...result
            }
        };

        self.postMessage(response);
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

    if (openDatabases.has(dbName)) {
        return { success: true };
    }

    try {
        // Use OpfsSAHPoolDb from the pool utility
        const db = new poolUtil.OpfsSAHPoolDb(`/databases/${dbName}`);
        openDatabases.set(dbName, db);

        console.log(`[SQLite Worker] Opened database: ${dbName} with OPFS SAHPool`);
        return { success: true };
    } catch (error) {
        console.error(`[SQLite Worker] Failed to open database ${dbName}:`, error);
        throw error;
    }
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
        // Bind parameters (convert object to array)
        const paramArray = Object.entries(parameters).map(([_, value]) => value);

        logger.debug(MODULE_NAME, 'Executing SQL:', sql.substring(0, 100));

        // Execute SQL - use returnValue to get the result
        const result = db.exec({
            sql: sql,
            bind: paramArray.length > 0 ? paramArray : undefined,
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
            rowsAffected = db.changes();
            lastInsertId = db.lastInsertRowId;
        }

        return {
            columnNames,
            columnTypes,
            rows: convertBigIntToString(result || []),
            rowsAffected,
            lastInsertId: convertBigIntToString(lastInsertId)
        };
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
        console.log(`[SQLite Worker] Closed database: ${dbName}`);
    }
    return { success: true };
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
            console.log(`[SQLite Worker] Deleted database: ${dbName}`);
        } else {
            console.warn(`[SQLite Worker] wipeFiles not available, database may persist`);
        }

        return { success: true };
    } catch (error) {
        console.error(`[SQLite Worker] Failed to delete database ${dbName}:`, error);
        throw error;
    }
}
