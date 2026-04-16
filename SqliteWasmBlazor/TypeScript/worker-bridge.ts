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

// ECDH key-wrapping state — lazily populated on first encrypted open
let sharedAesKey: CryptoKey | null = null;
let mainPubKeyRaw: Uint8Array | null = null;
let pendingKeyExchange: Promise<void> | null = null;
let resolveKeyExchange: (() => void) | null = null;

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

            // Complete ECDH key exchange when worker sends its public key
            if (event.data.type === 'workerPublicKey') {
                try {
                    const workerPubKey = await crypto.subtle.importKey(
                        'raw',
                        event.data.pub,
                        { name: 'ECDH', namedCurve: 'P-256' },
                        false,
                        []
                    );
                    const mainKeyPair = await crypto.subtle.generateKey(
                        { name: 'ECDH', namedCurve: 'P-256' },
                        true, // must be extractable to export the public key to the worker
                        ['deriveKey']
                    );
                    sharedAesKey = await crypto.subtle.deriveKey(
                        { name: 'ECDH', public: workerPubKey },
                        mainKeyPair.privateKey,
                        { name: 'AES-GCM', length: 256 },
                        false,
                        ['encrypt']
                    );
                    const rawPub = await crypto.subtle.exportKey('raw', mainKeyPair.publicKey);
                    mainPubKeyRaw = new Uint8Array(rawPub);
                    resolveKeyExchange?.();
                } catch (error) {
                    console.error('[Worker Bridge] ECDH key exchange failed:', error);
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
    }
})();

// Called from C# to send request to worker
// Async: if the message carries a SQLCipher key, performs ECDH key exchange (first call only)
// then encrypts the key with AES-GCM before posting. C# fires and forgets the returned Promise.
export async function sendToWorker(messageJson: string): Promise<void> {
    if (!worker) {
        throw new Error('Worker not initialized');
    }

    const message = JSON.parse(messageJson);

    // Encrypt SQLCipher key if present
    if (message.data?.key && typeof message.data.key === 'string' && message.data.key.length > 0) {
        // Lazy key exchange: trigger on first encrypted open
        if (!sharedAesKey) {
            if (!pendingKeyExchange) {
                pendingKeyExchange = new Promise<void>(resolve => {
                    resolveKeyExchange = resolve;
                });
                worker.postMessage({ type: 'initEncryption' });
            }
            await pendingKeyExchange;
        }

        const iv = crypto.getRandomValues(new Uint8Array(12));
        const ct = await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv },
            sharedAesKey!,
            new TextEncoder().encode(message.data.key)
        );
        delete message.data.key;
        message.data.encryptedKey = {
            iv: Array.from(iv),
            ct: Array.from(new Uint8Array(ct)),
            senderPub: Array.from(mainPubKeyRaw!)
        };
    }

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
    sendToWorker,
    sendBinaryToWorker
};

// Expose logger for C# JSImport
(globalThis as any).__sqliteWasmLogger = logger;
