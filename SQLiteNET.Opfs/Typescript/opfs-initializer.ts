// opfs-initializer.ts
// Main thread wrapper for OPFS Worker
// Handles communication with opfs-worker.js for file I/O

import { logger } from './opfs-logger';

interface InitializeResult {
    success: boolean;
    message: string;
    capacity?: number;
    fileCount?: number;
}

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

let worker: Worker | null = null;
let messageId = 0;
let pendingMessages = new Map<number, { resolve: Function; reject: Function }>();
let isInitialized = false;

function sendMessage(type: string, args?: any): Promise<any> {
    if (!worker) {
        return Promise.reject(new Error('Worker not initialized'));
    }

    return new Promise((resolve, reject) => {
        const id = messageId++;
        pendingMessages.set(id, { resolve, reject });

        const message: WorkerMessage = { id, type, args };
        worker!.postMessage(message);
    });
}

export async function initialize(): Promise<InitializeResult> {
    if (isInitialized) {
        return {
            success: true,
            message: 'Already initialized'
        };
    }

    try {
        logger.debug('OPFS Initializer', 'Creating worker...');

        // Create worker from bundled worker file
        worker = new Worker(
            '/_content/SQLiteNET.Opfs/opfs-worker.js',
            { type: 'module' }
        );

        // Set up message handler
        worker.onmessage = (event: MessageEvent<WorkerResponse>) => {
            const { id, success, result, error } = event.data;

            const pending = pendingMessages.get(id);
            if (pending) {
                pendingMessages.delete(id);
                if (success) {
                    pending.resolve(result);
                } else {
                    pending.reject(new Error(error || 'Unknown error'));
                }
            }
        };

        // Clean up handles before page unload
        window.addEventListener('beforeunload', () => {
            if (worker) {
                try {
                    // Send synchronous cleanup message (best effort)
                    worker.postMessage({ id: -1, type: 'cleanup' });
                } catch (e) {
                    logger.warn('OPFS Initializer', 'Could not send cleanup message:', e);
                }
            }
        });

        // Wait for worker ready signal
        await new Promise<void>((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('Worker initialization timeout')), 10000);

            worker!.addEventListener('message', function onReady(event) {
                if (event.data.type === 'ready') {
                    clearTimeout(timeout);
                    worker!.removeEventListener('message', onReady);
                    resolve();
                } else if (event.data.type === 'error') {
                    clearTimeout(timeout);
                    worker!.removeEventListener('message', onReady);
                    reject(new Error(event.data.error));
                }
            });
        });

        logger.debug('OPFS Initializer', 'Worker ready, getting capacity...');

        // Get initial capacity and file count
        const capacityResult = await sendMessage('getCapacity');
        const fileListResult = await sendMessage('getFileList');

        isInitialized = true;

        logger.debug('OPFS Initializer', 'Initialization complete');

        // Expose sendMessage globally for use by other modules (opfs-interop.ts)
        // This ensures they use the same worker instance
        (window as any).__opfsSendMessage = sendMessage;
        (window as any).__opfsIsInitialized = () => isInitialized;

        return {
            success: true,
            message: 'OPFS Worker initialized successfully',
            capacity: capacityResult.capacity,
            fileCount: fileListResult.files.length
        };
    } catch (error) {
        logger.error('OPFS Initializer', 'Failed to initialize:', error);
        return {
            success: false,
            message: error instanceof Error ? error.message : 'Unknown error'
        };
    }
}

export function isReady(): boolean {
    return isInitialized;
}

export async function getFileList(): Promise<string[]> {
    const result = await sendMessage('getFileList');
    return result.files || [];
}

export async function exportDatabase(filename: string): Promise<number[]> {
    const result = await sendMessage('readFile', { filename });
    return result.data;
}

export async function importDatabase(filename: string, dataArray: number[]): Promise<number> {
    const result = await sendMessage('writeFile', { filename, data: dataArray });
    return result.bytesWritten;
}

export async function deleteDatabase(filename: string): Promise<boolean> {
    const result = await sendMessage('deleteFile', { filename });
    return result.success;
}

export async function getCapacity(): Promise<number> {
    const result = await sendMessage('getCapacity');
    return result.capacity;
}

export async function addCapacity(count: number): Promise<number> {
    const result = await sendMessage('addCapacity', { count });
    return result.newCapacity;
}

/**
 * Persist a database file from Emscripten MEMFS to OPFS.
 * Reads the file from native WASM memory and stores it in OPFS.
 */
export async function persist(filename: string): Promise<void> {
    logger.debug('OPFS Persist', 'Starting persist for:', filename);

    // Check if file exists in Emscripten MEMFS
    if (!(window as any).Blazor?.runtime?.Module?.FS) {
        throw new Error('Emscripten FS not available. Ensure WasmBuildNative=true is enabled.');
    }

    const fs = (window as any).Blazor.runtime.Module.FS;
    const filePath = `/${filename}`;

    const pathInfo = fs.analyzePath(filePath);
    logger.debug('OPFS Persist', 'File exists in MEMFS:', pathInfo.exists);

    if (!pathInfo.exists) {
        throw new Error(`Database file ${filename} not found in MEMFS`);
    }

    // Read from MEMFS
    const data = fs.readFile(filePath);
    logger.debug('OPFS Persist', 'Read from MEMFS:', data.length, 'bytes');

    // Write to OPFS via worker
    await sendMessage('writeFile', {
        filename,
        data: Array.from(data)
    });

    logger.info('OPFS Persist', 'Written to OPFS:', filename);
}

/**
 * Persist only dirty pages from MEMFS to OPFS (incremental sync).
 * Used by VFS tracking for efficient partial updates.
 */
export async function persistDirtyPages(filename: string, pages: any[]): Promise<void> {
    logger.debug('OPFS Persist Dirty', 'Starting incremental persist for:', filename, '-', pages.length, 'pages');

    // Send dirty pages to worker for partial write
    const result = await sendMessage('persistDirtyPages', {
        filename,
        pages
    });

    logger.info('OPFS Persist Dirty', 'Written', result.pagesWritten, 'pages (', result.bytesWritten, 'bytes)');
}

/**
 * Load a database file from OPFS to Emscripten MEMFS.
 * Reads the file from OPFS and writes it to native WASM memory.
 */
export async function load(filename: string): Promise<void> {
    logger.debug('OPFS Load', 'Starting load for:', filename);

    if (!(window as any).Blazor?.runtime?.Module?.FS) {
        throw new Error('Emscripten FS not available. Ensure WasmBuildNative=true is enabled.');
    }

    const fs = (window as any).Blazor.runtime.Module.FS;
    const filePath = `/${filename}`;

    try {
        // Read from OPFS via worker
        const result = await sendMessage('readFile', { filename });
        logger.debug('OPFS Load', 'Read from OPFS:', result.data?.length || 0, 'bytes');

        if (result.data && result.data.length > 0) {
            // Write to MEMFS
            fs.writeFile(filePath, new Uint8Array(result.data));
            logger.info('OPFS Load', 'Written to MEMFS:', filename);
        }
    } catch (error) {
        // Database doesn't exist in OPFS yet - this is okay (will be created)
        logger.info('OPFS Load', `Database ${filename} not found in OPFS (will be created on first use)`);
    }
}

export async function cleanup(): Promise<void> {
    if (worker) {
        logger.debug('OPFS Initializer', 'Requesting cleanup...');
        try {
            await sendMessage('cleanup');
            logger.info('OPFS Initializer', 'Cleanup complete');
        } catch (e) {
            logger.warn('OPFS Initializer', 'Cleanup failed:', e);
        }
    }
}

/**
 * Send a message to the OPFS worker.
 * Exported for use by opfs-interop.ts (JSImport optimizations).
 */
export async function sendMessageToWorker(type: string, args?: any): Promise<any> {
    // Ensure worker is initialized
    if (!isInitialized) {
        await initialize();
    }

    return sendMessage(type, args);
}

logger.debug('OPFS Initializer', 'Module loaded');
