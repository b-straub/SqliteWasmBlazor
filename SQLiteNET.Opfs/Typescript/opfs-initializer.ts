// opfs-initializer.ts
// Main thread wrapper for OPFS Worker
// Handles communication with opfs-worker.js for file I/O

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
        console.log('[OPFS Initializer] Creating worker...');

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
                    console.warn('[OPFS Initializer] Could not send cleanup message:', e);
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

        console.log('[OPFS Initializer] Worker ready, getting capacity...');

        // Get initial capacity and file count
        const capacityResult = await sendMessage('getCapacity');
        const fileListResult = await sendMessage('getFileList');

        isInitialized = true;

        console.log('[OPFS Initializer] Initialization complete');

        return {
            success: true,
            message: 'OPFS Worker initialized successfully',
            capacity: capacityResult.capacity,
            fileCount: fileListResult.files.length
        };
    } catch (error) {
        console.error('[OPFS Initializer] Failed to initialize:', error);
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
    console.log('[OPFS Persist] Starting persist for:', filename);

    // Check if file exists in Emscripten MEMFS
    if (!(window as any).Blazor?.runtime?.Module?.FS) {
        throw new Error('Emscripten FS not available. Ensure WasmBuildNative=true is enabled.');
    }

    const fs = (window as any).Blazor.runtime.Module.FS;
    const filePath = `/${filename}`;

    const pathInfo = fs.analyzePath(filePath);
    console.log('[OPFS Persist] File exists in MEMFS:', pathInfo.exists);

    if (!pathInfo.exists) {
        throw new Error(`Database file ${filename} not found in MEMFS`);
    }

    // Read from MEMFS
    const data = fs.readFile(filePath);
    console.log('[OPFS Persist] Read from MEMFS:', data.length, 'bytes');

    // Write to OPFS via worker
    await sendMessage('writeFile', {
        filename,
        data: Array.from(data)
    });

    console.log('[OPFS Persist] Written to OPFS:', filename);
}

/**
 * Load a database file from OPFS to Emscripten MEMFS.
 * Reads the file from OPFS and writes it to native WASM memory.
 */
export async function load(filename: string): Promise<void> {
    console.log('[OPFS Load] Starting load for:', filename);

    if (!(window as any).Blazor?.runtime?.Module?.FS) {
        throw new Error('Emscripten FS not available. Ensure WasmBuildNative=true is enabled.');
    }

    const fs = (window as any).Blazor.runtime.Module.FS;
    const filePath = `/${filename}`;

    try {
        // Read from OPFS via worker
        const result = await sendMessage('readFile', { filename });
        console.log('[OPFS Load] Read from OPFS:', result.data?.length || 0, 'bytes');

        if (result.data && result.data.length > 0) {
            // Write to MEMFS
            fs.writeFile(filePath, new Uint8Array(result.data));
            console.log('[OPFS Load] Written to MEMFS:', filename);
        }
    } catch (error) {
        // Database doesn't exist in OPFS yet - this is okay (will be created)
        console.log(`[OPFS Load] Database ${filename} not found in OPFS (will be created on first use)`);
    }
}

export async function cleanup(): Promise<void> {
    if (worker) {
        console.log('[OPFS Initializer] Requesting cleanup...');
        try {
            await sendMessage('cleanup');
            console.log('[OPFS Initializer] Cleanup complete');
        } catch (e) {
            console.warn('[OPFS Initializer] Cleanup failed:', e);
        }
    }
}

console.log('[OPFS Initializer] Module loaded');
