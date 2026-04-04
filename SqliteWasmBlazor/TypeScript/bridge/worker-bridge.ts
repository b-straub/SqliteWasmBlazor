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

// Initialize worker on first import
(async () => {
    try {
        // Create worker - load from static assets path using base href
        const baseHref = document.querySelector('base')?.getAttribute('href') || '/';
        worker = new Worker(
            `${baseHref}_content/SqliteWasmBlazor/sqlite-wasm-worker.js`,
            { type: 'module' }
        );

        // Send base href to worker so it can locate WASM files
        worker.postMessage({ type: 'init', baseHref });

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
                // Intercept crypto responses (bridge→worker direct, not routed to C#)
                const cryptoResolve = cryptoPending.get(event.data.id);
                if (cryptoResolve) {
                    cryptoPending.delete(event.data.id);
                    cryptoResolve(JSON.stringify(event.data.data ?? event.data));
                    return;
                }

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

// ============================================================
// CRYPTO KEY MANAGEMENT (bridge → worker direct, no C# round-trip)
// Production: WebAuthn PRF seed → postMessage to worker
// Tests: random seed → same postMessage path
// ============================================================

let cryptoMsgId = 200000;
const cryptoPending = new Map<number, (json: string) => void>();

/**
 * Store crypto keys in the worker from a seed.
 * Returns JSON: { success, x25519PublicKeyBase64, ed25519PublicKeyBase64 }
 */
export function storeKeysInWorker(keyId: string, seedBase64: string, ttlMs: number): Promise<string> {
    return sendCryptoToWorker({ type: 'cryptoStoreKeys', keyId, seedBase64, ttlMs: ttlMs > 0 ? ttlMs : null });
}

/**
 * Remove keys from the worker's crypto cache.
 */
export function removeKeysFromWorker(keyId: string): Promise<string> {
    return sendCryptoToWorker({ type: 'cryptoRemoveKeys', keyId });
}

/**
 * Get public keys for a cached key set in the worker.
 */
export function getPublicKeysFromWorker(keyId: string): Promise<string> {
    return sendCryptoToWorker({ type: 'cryptoGetPublicKeys', keyId });
}

function sendCryptoToWorker(data: Record<string, unknown>): Promise<string> {
    if (!worker) {
        return Promise.reject(new Error('Worker not initialized'));
    }

    const id = ++cryptoMsgId;

    return new Promise<string>((resolve, reject) => {
        cryptoPending.set(id, resolve);

        const timer = setTimeout(() => {
            cryptoPending.delete(id);
            reject(new Error('Crypto worker operation timed out'));
        }, 30000);

        // Override timeout cleanup on resolve
        const wrappedResolve = (json: string) => {
            clearTimeout(timer);
            resolve(json);
        };
        cryptoPending.set(id, wrappedResolve);

        worker!.postMessage({ id, data });
    });
}

// Make functions available to C# JSImport
(globalThis as any).sqliteWasmWorker = {
    sendToWorker,
    sendBinaryToWorker,
    storeKeysInWorker,
    removeKeysFromWorker,
    getPublicKeysFromWorker
};

// Expose logger for C# JSImport
(globalThis as any).__sqliteWasmLogger = logger;
