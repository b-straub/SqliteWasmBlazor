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

// Wait for OPFS SAHPool to initialize
opfsSAHPool.isReady.then(() => {
    console.log('[OPFS Worker] SAHPool initialized, sending ready signal');
    self.postMessage({ type: 'ready' });
}).catch((error) => {
    console.error('[OPFS Worker] SAHPool initialization failed:', error);
    self.postMessage({ type: 'error', error: error.message });
});

// Handle messages from main thread
self.onmessage = async (event: MessageEvent<WorkerMessage>) => {
    const { id, type, args } = event.data;

    try {
        let result: any;

        switch (type) {
            case 'cleanup':
                // Release all OPFS handles before page unload
                console.log('[OPFS Worker] Cleaning up handles before unload...');
                opfsSAHPool.releaseAccessHandles();
                console.log('[OPFS Worker] Cleanup complete');
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
                    throw new Error(`File not found: ${args.filename}`);
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

console.log('[OPFS Worker] Worker script loaded, waiting for SAHPool initialization...');
