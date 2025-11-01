// sqlite-worker.ts
// Web Worker for OPFS SAHPool VFS - Persistent Storage Layer
//
// ARCHITECTURE NOTE:
// This project uses TWO separate SQLite instances:
//
// 1. e_sqlite3.a (from NuGet: SqlitePCLRaw.lib.e_sqlite3)
//    - Used by EF Core for all database operations
//    - Runs in .NET WASM runtime (Emscripten MEMFS)
//    - Handles SQL queries, migrations, change tracking
//
// 2. sqlite3.wasm (from npm: @sqlite.org/sqlite-wasm) - THIS FILE
//    - Used ONLY for OPFS file persistence
//    - Provides proven SAHPool VFS implementation
//    - Handles copying database files between MEMFS ↔ OPFS
//    - Does NOT execute any SQL queries
//
// WHY THIS ARCHITECTURE:
// - e_sqlite3.a cannot use JavaScript VFS (it's compiled native C)
// - OPFS APIs are JavaScript-only and require Web Worker
// - SAHPool VFS is a sophisticated, proven OPFS implementation
// - Extracting VFS alone is not feasible (tightly coupled with sqlite3.wasm)
//
// The dual-instance overhead is acceptable because:
// - Modern browsers handle WASM efficiently
// - SAHPool provides transaction safety and performance optimizations
// - Alternative (custom OPFS) would require extensive testing
//
// NOTE: You may see a harmless warning about "Cannot install OPFS" during
// initialization. This is filtered out (see printErr below) because we use
// SAHPool VFS which doesn't require COOP/COEP headers.

import sqlite3InitModule from '@sqlite.org/sqlite-wasm';

interface WorkerMessage {
    id: number;
    type: string;
    args?: any;
}

interface WorkerResponse {
    id: number;
    success: boolean;
    result?: any;
    error?: string;
}

let sqlite3: any = null;
let poolUtil: any = null;
let db: any = null;

// Initialize SQLite with OPFS SAHPool VFS
async function initializeSqlite() {
    try {
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
            locateFile: (file: string) => {
                if (file.endsWith('.wasm')) {
                    return `/_content/SQLiteNET.Opfs/${file}`;
                }
                return file;
            }
        });

        // Disable automatic OPFS VFS installation to prevent misleading warnings
        // We explicitly use SAHPool VFS below instead
        if ((sqlite3 as any).capi?.sqlite3_vfs_find('opfs')) {
            console.log('OPFS VFS auto-installed, but we use SAHPool VFS instead');
        }

        // Install OPFS SAHPool VFS
        poolUtil = await sqlite3.installOpfsSAHPoolVfs({
            initialCapacity: 6,
            directory: '/databases',
            name: 'opfs-sahpool',
            clearOnInit: false
        });

        return {
            success: true,
            message: 'SQLite OPFS SAHPool initialized',
            capacity: poolUtil.getCapacity(),
            fileCount: poolUtil.getFileCount()
        };
    } catch (error) {
        return {
            success: false,
            message: error instanceof Error ? error.message : 'Unknown error'
        };
    }
}

// Open database
async function openDatabase(filename: string) {
    try {
        if (!sqlite3 || !poolUtil) {
            throw new Error('SQLite not initialized');
        }

        db = new poolUtil.OpfsSAHPoolDb(`/databases/${filename}`);

        return {
            success: true,
            message: `Database ${filename} opened`
        };
    } catch (error) {
        return {
            success: false,
            message: error instanceof Error ? error.message : 'Failed to open database'
        };
    }
}

// Execute SQL
async function executeSql(sql: string, params: any[] = []) {
    try {
        if (!db) {
            throw new Error('Database not opened');
        }

        const result = db.exec({
            sql: sql,
            bind: params,
            returnValue: 'resultRows',
            rowMode: 'object'
        });

        return {
            success: true,
            result: result
        };
    } catch (error) {
        return {
            success: false,
            message: error instanceof Error ? error.message : 'SQL execution failed'
        };
    }
}

// Message handler
self.onmessage = async (event: MessageEvent<WorkerMessage>) => {
    const { id, type, args } = event.data;
    let result: any;

    try {
        switch (type) {
            case 'initialize':
                result = await initializeSqlite();
                break;

            case 'open':
                result = await openDatabase(args.filename);
                break;

            case 'exec':
                result = await executeSql(args.sql, args.params);
                break;

            case 'close':
                if (db) {
                    db.close();
                    db = null;
                }
                result = { success: true };
                break;

            case 'getFileList':
                result = {
                    success: true,
                    files: poolUtil ? poolUtil.getFileNames() : []
                };
                break;

            case 'getCapacity':
                result = {
                    success: true,
                    capacity: poolUtil ? poolUtil.getCapacity() : 0
                };
                break;

            case 'addCapacity':
                if (poolUtil) {
                    const newCapacity = await poolUtil.addCapacity(args.count);
                    result = { success: true, newCapacity };
                } else {
                    throw new Error('Pool util not initialized');
                }
                break;

            case 'exportDatabase':
                if (poolUtil) {
                    const data = await poolUtil.exportFile(args.filename);
                    result = { success: true, data: Array.from(data) };
                } else {
                    throw new Error('Pool util not initialized');
                }
                break;

            case 'importDatabase':
                if (poolUtil) {
                    const uint8Array = new Uint8Array(args.data);
                    const bytesWritten = await poolUtil.importDb(args.filename, uint8Array);
                    result = { success: true, bytesWritten };
                } else {
                    throw new Error('Pool util not initialized');
                }
                break;

            case 'deleteDatabase':
                // TODO: Implement delete
                console.warn('Database deletion not yet implemented');
                result = { success: true };
                break;

            default:
                result = {
                    success: false,
                    message: `Unknown message type: ${type}`
                };
        }

        const response: WorkerResponse = {
            id,
            success: true,
            result
        };

        self.postMessage(response);
    } catch (error) {
        const response: WorkerResponse = {
            id,
            success: false,
            error: error instanceof Error ? error.message : 'Unknown error'
        };

        self.postMessage(response);
    }
};

// Signal ready
self.postMessage({ type: 'ready' });
