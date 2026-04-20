// worker-bridge.ts
// Bridge between C# JSImport and Web Worker

/**
 * IMemoryView interface from dotnet runtime
 * Represents a view over managed memory (Span/ArraySegment)
 */
interface IMemoryView {
    slice(): Uint8Array;
    slice(start: number): Uint8Array;
    slice(start: number, end: number): Uint8Array;
}

let worker: Worker | null = null;

// Called from C# (JSImport) to create the worker with explicit asset paths.
// Avoids reading <base href> from the DOM, which is blocked by strict CSP and
// unreliable in browser-extension contexts.
export function initializeBridge(baseHref: string, assetRoot: string): void {
    try {
        worker = new Worker(
            `${baseHref}${assetRoot}sqlite-wasm-worker.js`,
            { type: 'module' }
        );

        // Pass both paths so the worker can locate .wasm and other assets.
        worker.postMessage({ type: 'init', baseHref, assetRoot });

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

                    // Check if raw binary data (export operations)
                    if (event.data.rawBinary && event.data.data instanceof Uint8Array) {
                        exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponseRawBinary(
                            event.data.id,
                            event.data.data
                        );
                    }
                    // Check if binary MessagePack data
                    else if (event.data.binary && event.data.data instanceof Uint8Array) {
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
                    // Notify C# that the request failed so TaskCompletionSource doesn't hang
                    try {
                        const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");
                        const errorJson = JSON.stringify({
                            id: event.data.id,
                            data: { success: false, error: `Bridge callback failed: ${error}` }
                        });
                        exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponse(errorJson);
                    } catch {
                        // Last resort — can't notify C#
                    }
                }
            }
        };

        worker.onerror = (error) => {
            console.error('[Worker Bridge] Worker error event:', error);
        };

    } catch (error) {
        console.error('[Worker Bridge] Failed to create worker:', error);
        // Notify C# so InitializeAsync throws instead of timing out.
        try {
            const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");
            exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerError(
                error instanceof Error ? error.message : 'Failed to create worker'
            );
        } catch {
            // Runtime not yet available — nothing more we can do.
        }
    }
}

// Called from C# to send request to worker
export function sendToWorker(messageJson: string): void {
    if (!worker) {
        throw new Error('Worker not initialized');
    }

    const message = JSON.parse(messageJson);
    worker.postMessage(message);
}

// Called from C# to send binary data to worker (import operations)
export function sendBinaryToWorker(memoryView: IMemoryView, metadataJson: string): void {
    if (!worker) {
        throw new Error('Worker not initialized');
    }

    // Slice MemoryView to get a copy as Uint8Array
    const data = memoryView.slice();
    const metadata = JSON.parse(metadataJson);

    // Post with transferable buffer for zero-copy transfer to worker
    worker.postMessage(
        { ...metadata, binaryPayload: data.buffer },
        [data.buffer]
    );
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
    initializeBridge,
    sendToWorker,
    sendBinaryToWorker
};

// Expose logger for C# JSImport
(globalThis as any).__sqliteWasmLogger = logger;
