// worker-bridge.ts
// Bridge between C# JSImport and Web Worker

let worker: Worker | null = null;

// Initialize worker on first import
(async () => {
    try {
        // Create worker - load from static assets path
        worker = new Worker(
            '/_content/SqliteWasmBlazor/sqlite-wasm-worker.js',
            { type: 'module' }
        );

        // Handle messages from worker
        worker.onmessage = async (event) => {
            if (event.data.type === 'ready') {
                console.log('[Worker Bridge] Worker ready');
                try {
                    const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");
                    exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerReady();
                } catch (error) {
                    console.error('[Worker Bridge] Failed to call OnWorkerReady:', error);
                }
                return;
            }

            if (event.data.type === 'error') {
                console.error('[Worker Bridge] Worker error:', event.data.error);
                try {
                    const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");
                    exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerError(event.data.error || 'Unknown worker error');
                } catch (error) {
                    console.error('[Worker Bridge] Failed to call OnWorkerError:', error);
                }
                return;
            }

            // Forward response to C# via JSExport method
            if (event.data.id !== undefined) {
                try {
                    const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");

                    // Check if binary MessagePack data
                    if (event.data.binary && event.data.data instanceof Uint8Array) {
                        // Zero-copy binary path: Uint8Array → Span<byte>
                        exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponseBinary(
                            event.data.id,
                            event.data.data
                        );
                    } else {
                        // JSON fallback for non-execute operations and errors
                        const messageJson = JSON.stringify(event.data);
                        exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponse(messageJson);
                    }
                } catch (error) {
                    console.error('[Worker Bridge] Failed to call C# callback:', error);
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
    sendToWorker
};

// Expose logger for C# JSImport
(globalThis as any).__sqliteWasmLogger = logger;
