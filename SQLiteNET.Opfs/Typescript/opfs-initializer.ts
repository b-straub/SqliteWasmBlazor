// opfs-initializer.ts
// Main thread wrapper for SQLite Worker with OPFS SAHPool

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
        // Create worker from bundled worker file in wwwroot
        worker = new Worker(
            '/_content/SQLiteNET.Opfs/sqlite-worker.js',
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

        // Wait for worker ready signal
        await new Promise<void>((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('Worker initialization timeout')), 10000);

            worker!.addEventListener('message', function onReady(event) {
                if (event.data.type === 'ready') {
                    clearTimeout(timeout);
                    worker!.removeEventListener('message', onReady);
                    resolve();
                }
            });
        });

        // Initialize SQLite in worker
        const initResult = await sendMessage('initialize');

        if (!initResult.success) {
            throw new Error(initResult.message);
        }

        isInitialized = true;

        return {
            success: true,
            message: initResult.message,
            capacity: initResult.capacity,
            fileCount: initResult.fileCount
        };
    } catch (error) {
        console.error('Failed to initialize SQLite OPFS:', error);
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
    const result = await sendMessage('exportDatabase', { filename });
    return result.data;
}

export async function importDatabase(filename: string, dataArray: number[]): Promise<number> {
    const result = await sendMessage('importDatabase', { filename, data: dataArray });
    return result.bytesWritten;
}

export async function deleteDatabase(filename: string): Promise<boolean> {
    const result = await sendMessage('deleteDatabase', { filename });
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
 * @param filename Database filename (e.g., "TodoDb.db")
 */
export async function persist(filename: string): Promise<void> {
    // Check if file exists in Emscripten MEMFS
    if (!(window as any).Blazor?.runtime?.Module?.FS) {
        throw new Error('Emscripten FS not available. Ensure WasmBuildNative=true is enabled.');
    }

    const fs = (window as any).Blazor.runtime.Module.FS;
    const filePath = `/${filename}`;

    if (!fs.analyzePath(filePath).exists) {
        throw new Error(`Database file ${filename} not found in MEMFS`);
    }

    // Read from MEMFS
    const data = fs.readFile(filePath);

    // Write to OPFS via worker
    await sendMessage('importDatabase', {
        filename,
        data: Array.from(data)
    });
}

/**
 * Load a database file from OPFS to Emscripten MEMFS.
 * Reads the file from OPFS and writes it to native WASM memory.
 * @param filename Database filename (e.g., "TodoDb.db")
 */
export async function load(filename: string): Promise<void> {
    if (!(window as any).Blazor?.runtime?.Module?.FS) {
        throw new Error('Emscripten FS not available. Ensure WasmBuildNative=true is enabled.');
    }

    const fs = (window as any).Blazor.runtime.Module.FS;
    const filePath = `/${filename}`;

    try {
        // Read from OPFS via worker
        const result = await sendMessage('exportDatabase', { filename });

        if (result.data && result.data.length > 0) {
            // Write to MEMFS
            fs.writeFile(filePath, new Uint8Array(result.data));
        }
    } catch (error) {
        // Database doesn't exist in OPFS yet - this is okay (will be created)
        console.log(`Database ${filename} not found in OPFS (will be created on first use)`);
    }
}
