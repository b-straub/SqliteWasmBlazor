// worker-bridge.ts
// Bridge between C# JSImport and Web Worker.
// Exposes a single async initializeBridge(baseHref, assetRoot) entry point;
// C# awaits its returned Promise so worker creation errors surface on the .NET side.

import { base64ToBytes } from '@sqlitewasmblazor/crypto-core';

import {
    packArrayHeader,
    packBinHeader,
    packStr,
    packUint,
} from './msgpack-stream';

/**
 * IMemoryView interface from dotnet runtime — view over managed Span/ArraySegment.
 */
interface IMemoryView {
    slice(): Uint8Array;
    slice(start: number): Uint8Array;
    slice(start: number, end: number): Uint8Array;
}

let worker: Worker | null = null;

/**
 * JS-side stream handler registry — separate from the C# request-id space.
 * Streaming worker calls (currently only the encrypted disk export) post
 * a sequence of `streamChunk` messages followed by `streamDone`, all keyed
 * by `streamId`. Until streamDone arrives the handler stays installed,
 * accumulating the per-DB rekey output as standalone Blobs (so each one
 * can be released from JS heap and disk-backed by Safari independently).
 */
interface StreamHandler {
    onChunk(name: string, data: Uint8Array): void;
    onDone(result?: number): void;
    onError(message: string): void;
}
const streamHandlers = new Map<number, StreamHandler>();
let nextStreamId = -1; // negative IDs sit clear of the C#-side _nextRequestId (which only ever increments positively).

/**
 * Create the Web Worker and wire up message handling.
 * Called from C# via JSImport after JSHost.ImportAsync has loaded this module.
 * Returns a resolved Promise once the Worker is constructed — the worker's own
 * "ready" signal arrives asynchronously via postMessage → OnWorkerReady.
 */
export async function initializeBridge(baseHref: string, assetRoot: string): Promise<void> {
    worker = new Worker(
        `${baseHref}${assetRoot}sqlite-wasm-worker.js`,
        { type: 'module' }
    );

    worker.postMessage({ type: 'init', baseHref, assetRoot });

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

        // Streaming responses — keyed by `streamId`, dispatched JS-side to
        // a handler in `streamHandlers`. Worker emits a sequence of
        // streamChunk → ... → streamDone (or streamError) all under the same
        // streamId. C# never sees these messages.
        if (event.data.streamId !== undefined) {
            const handler = streamHandlers.get(event.data.streamId);
            if (!handler) {
                console.warn(
                    '[Worker Bridge] Stream message for unknown streamId',
                    event.data.streamId);
                return;
            }
            if (event.data.streamChunk === true) {
                handler.onChunk(event.data.name as string, event.data.data as Uint8Array);
            } else if (event.data.streamDone === true) {
                handler.onDone(
                    typeof event.data.result === 'number' ? event.data.result : undefined);
            } else if (event.data.streamError === true) {
                handler.onError(
                    typeof event.data.error === 'string' ? event.data.error : 'unknown stream error');
            } else {
                console.warn('[Worker Bridge] Unknown stream message shape', event.data);
            }
            return;
        }

        if (event.data.id !== undefined) {
            try {
                const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");

                if (event.data.rawBinary && event.data.data instanceof Uint8Array) {
                    exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponseRawBinary(
                        event.data.id,
                        event.data.data
                    );
                } else if (event.data.binary && event.data.data instanceof Uint8Array) {
                    exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponseBinary(
                        event.data.id,
                        event.data.data
                    );
                } else {
                    const messageJson = JSON.stringify(event.data);
                    exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponse(messageJson);
                }
            } catch (error) {
                console.error('[Worker Bridge] Failed to call C# callback:', error);
                try {
                    const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");
                    const errorJson = JSON.stringify({
                        id: event.data.id,
                        data: { success: false, error: `Bridge callback failed: ${error}` }
                    });
                    exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerResponse(errorJson);
                } catch {
                    // Last resort — runtime unavailable, can't notify C#.
                }
            }
        }
    };

    worker.onerror = (error) => {
        console.error('[Worker Bridge] Worker error event:', error);
    };
}

/** Send a JSON request to the worker (C# → worker). */
export function sendToWorker(messageJson: string): void {
    if (!worker) {
        throw new Error('Worker not initialized');
    }

    const message = JSON.parse(messageJson);
    worker.postMessage(message);
}

// Called from C# to send binary data to worker (import operations)
// Optional header: small binary (nonce+key) sent alongside large payload without copying payload.
export function sendBinaryToWorker(memoryView: IMemoryView, metadataJson: string, headerView?: IMemoryView): void {
    if (!worker) {
        throw new Error('Worker not initialized');
    }

    const data = memoryView.slice();
    const metadata = JSON.parse(metadataJson);

    if (headerView) {
        const header = headerView.slice();
        // Transfer both buffers — header carries CryptoHeader private-key
        // material; transferring synchronously detaches the JS-side copy on
        // the main thread so no readable reference survives postMessage.
        worker.postMessage(
            { ...metadata, binaryHeader: header.buffer, binaryPayload: data.buffer },
            [data.buffer, header.buffer]
        );
    } else {
        worker.postMessage(
            { ...metadata, binaryPayload: data.buffer },
            [data.buffer]
        );
    }
}

/**
 * Encrypted-disk envelope export, streaming variant. Replaces the legacy
 * "C# returns byte[]" flow that OOM'd mobile browsers on ~250 MB DBs.
 * Drives the worker via a streaming protocol: each rekeyed DB lands on
 * the main thread as its own Blob (which the browser can disk-back)
 * instead of being marshaled into a managed byte[] then re-copied for
 * the Blob download.
 *
 * Memory peak shifts from ~3× envelope-size (worker pack + C# byte[] + JS
 * Blob copy) down to ~1× the largest single DB transient during one
 * rekey + assembly. See project_mobile_export_memory_profile.md.
 *
 * @param filename Suggested download filename (passed straight to `<a download>`).
 * @param metadataJson JSON-encoded envelope header fields: { version,
 *   aadVersion, ephemeralPublicKey, wrappedContentKeyCiphertext,
 *   wrappedContentKeyNonce, credentialIdHint }. All strings are Base64
 *   except `version` (positive int).
 * @param kWrapView 32-byte ChaCha20 wrap key, transferred to the worker
 *   wrapped in a VfsKeyHeader so the existing rekey path consumes it.
 */
export function exportDiskToDownload(
    filename: string,
    metadataJson: string,
    kWrapView: IMemoryView,
): Promise<boolean> {
    return _assembleEnvelopeStreamed(metadataJson, kWrapView).then((blob) => {
        triggerEnvelopeDownload(filename, blob);
        return true;
    });
}

/**
 * Shared assembly path: drives the worker streaming export, accumulates
 * each rekeyed DB as a standalone Blob on the main thread, composes the
 * MessagePack <c>EncryptedDiskEnvelope</c> Blob via the positional
 * encoder.
 */
function _assembleEnvelopeStreamed(
    metadataJson: string,
    kWrapView: IMemoryView,
): Promise<Blob> {
    if (!worker) {
        return Promise.reject(new Error('Worker not initialized'));
    }
    const meta = JSON.parse(metadataJson) as {
        version: number;
        aadVersion: string;
        prfSaltBase64: string;
        ephemeralPublicKey: string;
        wrappedContentKeyCiphertext: string;
        wrappedContentKeyNonce: string;
        credentialIdHint: string;
    };

    const streamId = nextStreamId--;
    const kWrap = kWrapView.slice();
    const fileParts: { name: string; size: number; blob: Blob }[] = [];

    return new Promise((resolve, reject) => {
        streamHandlers.set(streamId, {
            onChunk(name, data) {
                // Wrap each rekeyed Uint8Array as its own Blob. Dropping the
                // Uint8Array reference (returning from this callback) lets
                // Safari hold the bytes in a disk-backed Blob instead of
                // pinning them in JS heap — that's the central memory win.
                fileParts.push({ name, size: data.length, blob: new Blob([data]) });
            },
            onDone() {
                streamHandlers.delete(streamId);
                try {
                    resolve(composeEnvelopeBlob(meta, fileParts));
                } catch (e) {
                    reject(e instanceof Error ? e : new Error(String(e)));
                }
            },
            onError(message) {
                streamHandlers.delete(streamId);
                reject(new Error(message));
            },
        });

        // Transfer K_wrap into the worker — the buffer detaches from the
        // main side immediately, matching the existing sendBinaryToWorker
        // ownership semantics. `data: { type }` matches the legacy
        // WorkerRequest shape the worker's onmessage destructures.
        worker!.postMessage(
            {
                streamId,
                data: { type: 'exportDiskStream' },
                binaryPayload: kWrap.buffer,
            },
            [kWrap.buffer],
        );
    });
}

/**
 * Streaming asymmetric disk import — preflight phase. Builds a Blob from
 * <paramref name="envelopeView"/> (the .eds bytes the C# side reads off
 * disk), posts the Blob + K_wrap to the worker, awaits a streamDone
 * with the <c>DiskImportResult</c> int. No pool mutation happens during
 * preflight — C# service follows up with wipe + EnterEncryptedAsync only
 * if this returns OK (0).
 */
export function importDiskStreamPreflight(
    envelopeView: IMemoryView,
    kWrapView: IMemoryView,
): Promise<number> {
    return _sendImportDiskStream('importDiskStreamPreflight', envelopeView, kWrapView);
}

/**
 * Streaming asymmetric disk import — commit phase. C# caller must have
 * wiped the pool and registered the new globalKey via EnterEncryptedAsync
 * before invoking this. Worker reads the envelope's Files section
 * slot-by-slot, decrypts under K_wrap, re-encrypts under globalKey, and
 * hands each rekeyed file to <c>poolUtil.importDb</c>. Resolves with 0
 * on success; throws on AEAD failure mid-commit (caller's pool is now
 * partially-imported but already-wiped — same window the legacy import
 * had).
 */
export function importDiskStreamCommit(
    envelopeView: IMemoryView,
    kWrapView: IMemoryView,
): Promise<number> {
    return _sendImportDiskStream('importDiskStreamCommit', envelopeView, kWrapView);
}

/**
 * Internal helper shared by preflight + commit: build a Blob from
 * envelope bytes, transfer K_wrap to the worker, await the streamDone
 * result. The envelope `Uint8Array` is dropped from JS scope once the
 * Blob retains a reference, so the browser can disk-back it on memory
 * pressure — that's what keeps the import workable below the WASM
 * linear-memory ceiling.
 */
function _sendImportDiskStream(
    type: 'importDiskStreamPreflight' | 'importDiskStreamCommit',
    envelopeView: IMemoryView,
    kWrapView: IMemoryView,
): Promise<number> {
    if (!worker) {
        return Promise.reject(new Error('Worker not initialized'));
    }
    const envelopeBytes = envelopeView.slice();
    const blob = new Blob([envelopeBytes]);
    const kWrap = kWrapView.slice();
    const streamId = nextStreamId--;
    return new Promise((resolve, reject) => {
        streamHandlers.set(streamId, {
            onChunk() {
                streamHandlers.delete(streamId);
                reject(new Error(`Unexpected streamChunk during ${type}`));
            },
            onDone(result) {
                streamHandlers.delete(streamId);
                if (typeof result !== 'number') {
                    reject(new Error(`${type} streamDone missing result`));
                    return;
                }
                resolve(result);
            },
            onError(message) {
                streamHandlers.delete(streamId);
                reject(new Error(message));
            },
        });
        // Worker's onmessage destructures `data: {type}` (legacy
        // sendBinaryToWorker shape) — keep the type nested so the
        // existing switch dispatch works without a special-case.
        worker!.postMessage(
            {
                streamId,
                data: { type },
                blob,
                binaryPayload: kWrap.buffer,
            },
            [kWrap.buffer],
        );
    });
}

/**
 * Compose the EncryptedDiskEnvelope wire shape as a Blob — header bytes +
 * per-DB Blob parts — and trigger the download via an anchor click. The
 * Blob is a virtual concatenation; Safari materialises chunks on read,
 * disk-backing the per-DB segments so the full envelope never lives in
 * JS heap as one buffer.
 */
/**
 * Compose the EncryptedDiskEnvelope v3 wire shape as a Blob — positional
 * MessagePack-CSharp <c>[Key(N)]</c> record, decoded by ImportDiskAsync
 * via <c>MessagePackSerializer.Deserialize&lt;EncryptedDiskEnvelope&gt;</c>.
 * Returns a virtual-concatenation Blob; the per-DB segments stay
 * referenced as standalone Blob parts so the browser can disk-back them.
 * Wire layout:
 *   [0] Version (int) = 3
 *   [1] AadVersion (string)
 *   [2] PrfSalt (bin, 32 bytes)
 *   [3] EphemeralPublicKey (string, Base64)
 *   [4] WrappedContentKeyCiphertext (string, Base64)
 *   [5] WrappedContentKeyNonce (string, Base64)
 *   [6] CredentialIdHint (string, Base64)
 *   [7] Files (List&lt;EncryptedDiskFile&gt;) — each [Name(str), Bytes(bin)]
 */
function composeEnvelopeBlob(
    meta: {
        version: number;
        aadVersion: string;
        prfSaltBase64: string;
        ephemeralPublicKey: string;
        wrappedContentKeyCiphertext: string;
        wrappedContentKeyNonce: string;
        credentialIdHint: string;
    },
    fileParts: { name: string; size: number; blob: Blob }[],
): Blob {
    const prfSaltBytes = base64ToBytes(meta.prfSaltBase64);
    if (prfSaltBytes.length !== 32) {
        throw new Error(
            `composeEnvelopeBlob: prfSalt must decode to 32 bytes (got ${prfSaltBytes.length})`);
    }
    const parts: BlobPart[] = [];
    parts.push(packArrayHeader(8));
    parts.push(packUint(meta.version));
    parts.push(...packStr(meta.aadVersion));
    parts.push(packBinHeader(prfSaltBytes.length));
    parts.push(prfSaltBytes);
    parts.push(...packStr(meta.ephemeralPublicKey));
    parts.push(...packStr(meta.wrappedContentKeyCiphertext));
    parts.push(...packStr(meta.wrappedContentKeyNonce));
    parts.push(...packStr(meta.credentialIdHint));
    parts.push(packArrayHeader(fileParts.length));
    for (const f of fileParts) {
        parts.push(packArrayHeader(2));
        parts.push(...packStr(f.name));
        parts.push(packBinHeader(f.size));
        parts.push(f.blob);
    }
    return new Blob(parts, { type: 'application/x-msgpack' });
}

function triggerEnvelopeDownload(filename: string, envelope: Blob): void {
    const url = URL.createObjectURL(envelope);
    try {
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        link.style.display = 'none';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    } finally {
        URL.revokeObjectURL(url);
    }
}

export const logger = {
    setLogLevel(level: number): void {
        if (!worker) {
            console.warn('[Worker Bridge] Worker not initialized, cannot set log level');
            return;
        }
        worker.postMessage({
            type: 'setLogLevel',
            level: level
        });
    }
};

(globalThis as any).sqliteWasmWorker = {
    initializeBridge,
    sendToWorker,
    sendBinaryToWorker,
    exportDiskToDownload,
    importDiskStreamPreflight,
    importDiskStreamCommit,
};

(globalThis as any).__sqliteWasmLogger = logger;
