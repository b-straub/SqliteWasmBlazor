// worker-bridge.ts
// Bridge between C# JSImport and Web Worker

let worker: Worker | null = null;
let readyCallback: (() => void) | null = null;

// Initialize worker on first import
(async () => {
    try {
        // Create worker - load from static assets path
        worker = new Worker(
            '/_content/System.Data.SQLite.Wasm/sqlite-wasm-worker.js',
            { type: 'module' }
        );

        // Handle messages from worker
        worker.onmessage = async (event) => {
            if (event.data.type === 'ready') {
                console.log('[Worker Bridge] Worker ready');
                if (readyCallback) {
                    readyCallback();
                }
                return;
            }

            if (event.data.type === 'error') {
                console.error('[Worker Bridge] Worker error:', event.data.error);
                return;
            }

            // Forward response to C# via JSExport method
            if (event.data.id !== undefined) {
                // Serialize to JSON once, C# deserializes with source-generated context
                const messageJson = JSON.stringify(event.data);
                try {
                    const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("System.Data.SQLite.Wasm.dll");
                    exports.System.Data.SQLite.Wasm.SqliteWasmWorkerBridge.OnWorkerResponse(messageJson);
                } catch (error) {
                    console.error('[Worker Bridge] Failed to call C# OnWorkerResponse:', error);
                }
            }
        };

        worker.onerror = (error) => {
            console.error('[Worker Bridge] Worker error event:', error);
        };

    } catch (error) {
        console.error('[Worker Bridge] Failed to create worker:', error);
    }
})();

// Called from C# to send request to worker
export function sendToWorker(messageJson: string): void {
    if (!worker) {
        throw new Error('Worker not initialized');
    }

    const message = JSON.parse(messageJson);
    worker.postMessage(message);
}

// Called from C# to set ready callback
export function setReadyCallback(callback: () => void): void {
    readyCallback = callback;
}

// Logger API - matches C# SqliteWasmLogLevel enum
export const logger = {
    setLogLevel(level: number): void {
        if (!worker) {
            console.warn('[Worker Bridge] Worker not initialized, cannot set log level');
            return;
        }
        // Send log level change to worker
        worker.postMessage({
            type: 'setLogLevel',
            level: level
        });
    }
};

// Make functions available to C# JSImport
(globalThis as any).sqliteWasmWorker = {
    sendToWorker,
    setReadyCallback
};

// Expose logger for C# JSImport
(globalThis as any).__sqliteWasmLogger = logger;
