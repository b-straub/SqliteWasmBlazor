// opfs-worker.ts
// Web Worker for OPFS file I/O using SAHPool
// Handles only file read/write operations - no SQL execution

import { opfsSAHPool } from './opfs-sahpool';

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

// Simple logger for worker context (matches OpfsLogLevel enum)
enum LogLevel { None = 0, Error = 1, Warning = 2, Info = 3, Debug = 4 }
let workerLogLevel = LogLevel.Warning; // Default

const log = {
    debug: (...args: any[]) => workerLogLevel >= LogLevel.Debug && console.log('[OPFS Worker]', ...args),
    info: (...args: any[]) => workerLogLevel >= LogLevel.Info && console.log('[OPFS Worker] ✓', ...args),
    warn: (...args: any[]) => workerLogLevel >= LogLevel.Warning && console.warn('[OPFS Worker] ⚠', ...args),
    error: (...args: any[]) => workerLogLevel >= LogLevel.Error && console.error('[OPFS Worker] ❌', ...args)
};

// Wait for OPFS SAHPool to initialize
opfsSAHPool.isReady.then(() => {
    log.info('SAHPool initialized, sending ready signal');
    self.postMessage({ type: 'ready' });
}).catch((error) => {
    log.error('SAHPool initialization failed:', error);
    self.postMessage({ type: 'error', error: error.message });
});

// Handle messages from main thread
self.onmessage = async (event: MessageEvent<WorkerMessage>) => {
    const { id, type, args } = event.data;

    try {
        let result: any;

        switch (type) {
            case 'setLogLevel':
                // Configure log level for worker and SAHPool
                workerLogLevel = args.level;
                opfsSAHPool.logLevel = args.level;
                log.info(`Log level set to ${args.level}`);
                result = { success: true };
                break;

            case 'cleanup':
                // Release all OPFS handles before page unload
                log.info('Cleaning up handles before unload...');
                opfsSAHPool.releaseAccessHandles();
                log.info('Cleanup complete');
                result = { success: true };
                break;

            case 'getCapacity':
                result = {
                    capacity: opfsSAHPool.getCapacity()
                };
                break;

            case 'addCapacity':
                result = {
                    newCapacity: await opfsSAHPool.addCapacity(args.count)
                };
                break;

            case 'getFileList':
                result = {
                    files: opfsSAHPool.getFileNames()
                };
                break;

            case 'readFile':
                // Read file from OPFS using SAHPool
                const fileId = opfsSAHPool.xOpen(args.filename, 0x01); // READONLY
                if (fileId < 0) {
                    // File doesn't exist yet - this is normal on first run
                    log.debug(`File not found in OPFS: ${args.filename} (will be created on first write)`);
                    result = {
                        data: [] // Return empty array to indicate file doesn't exist
                    };
                    break;
                }

                const size = opfsSAHPool.xFileSize(fileId);
                const buffer = new Uint8Array(size);
                const readResult = opfsSAHPool.xRead(fileId, buffer, size, 0);
                opfsSAHPool.xClose(fileId);

                if (readResult !== 0) {
                    throw new Error(`Failed to read file: ${args.filename}`);
                }

                result = {
                    data: Array.from(buffer)
                };
                break;

            case 'writeFile':
                // Write file to OPFS using SAHPool
                const data = new Uint8Array(args.data);
                const writeFileId = opfsSAHPool.xOpen(
                    args.filename,
                    0x02 | 0x04 | 0x100 // READWRITE | CREATE | MAIN_DB
                );

                if (writeFileId < 0) {
                    throw new Error(`Failed to open file for writing: ${args.filename}`);
                }

                // Truncate to exact size
                opfsSAHPool.xTruncate(writeFileId, data.length);

                // Write data
                const writeResult = opfsSAHPool.xWrite(writeFileId, data, data.length, 0);

                // Sync to disk
                opfsSAHPool.xSync(writeFileId, 0);
                opfsSAHPool.xClose(writeFileId);

                if (writeResult !== 0) {
                    throw new Error(`Failed to write file: ${args.filename}`);
                }

                result = {
                    bytesWritten: data.length
                };
                break;

            case 'persistDirtyPages':
                // Write only dirty pages to OPFS (incremental sync)
                const { filename, pages } = args;

                if (!pages || pages.length === 0) {
                    result = { pagesWritten: 0 };
                    break;
                }

                log.info(`Persisting ${pages.length} dirty pages for ${filename}`);

                const PAGE_SIZE = 4096;
                const SQLITE_OK = 0;
                const FLAGS_READWRITE = 0x02;
                const FLAGS_CREATE = 0x04;
                const FLAGS_MAIN_DB = 0x100;

                // Open file for partial writes (create if it doesn't exist yet)
                const partialFileId = opfsSAHPool.xOpen(
                    filename,
                    FLAGS_READWRITE | FLAGS_CREATE | FLAGS_MAIN_DB
                );

                if (partialFileId < 0) {
                    throw new Error(`Failed to open file for partial write: ${filename}`);
                }

                let pagesWritten = 0;

                try {
                    // Write each dirty page
                    for (const page of pages) {
                        const { pageNumber, data } = page;
                        const offset = pageNumber * PAGE_SIZE;
                        const pageBuffer = new Uint8Array(data);

                        const writeRc = opfsSAHPool.xWrite(
                            partialFileId,
                            pageBuffer,
                            pageBuffer.length,
                            offset
                        );

                        if (writeRc !== SQLITE_OK) {
                            throw new Error(`Failed to write page ${pageNumber} at offset ${offset}`);
                        }

                        pagesWritten++;
                    }

                    // Sync to ensure data is persisted
                    opfsSAHPool.xSync(partialFileId, 0);

                    log.info(`Successfully wrote ${pagesWritten} pages`);

                } finally {
                    // Always close the file
                    opfsSAHPool.xClose(partialFileId);
                }

                result = {
                    pagesWritten,
                    bytesWritten: pagesWritten * PAGE_SIZE
                };
                break;

            case 'deleteFile':
                const deleteResult = opfsSAHPool.xDelete(args.filename, 1);
                if (deleteResult !== 0) {
                    throw new Error(`Failed to delete file: ${args.filename}`);
                }
                result = { success: true };
                break;

            case 'fileExists':
                const exists = opfsSAHPool.xAccess(args.filename, 0) === 0;
                result = { exists };
                break;

            default:
                throw new Error(`Unknown message type: ${type}`);
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

log.info('Worker script loaded, waiting for SAHPool initialization...');
