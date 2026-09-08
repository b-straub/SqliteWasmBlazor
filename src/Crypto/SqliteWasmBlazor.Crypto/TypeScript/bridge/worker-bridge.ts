// worker-bridge.ts
// Bridge between C# JSImport and Web Worker.
// Exposes a single async initializeBridge(baseHref, assetRoot) entry point;
// C# awaits its returned Promise so worker creation errors surface on the .NET side.

import { base64ToBytes } from '@sqlitewasmblazor/crypto-core';
import {
    createStreamRouter,
    deleteStagedExportFile,
    downloadStagedExport,
    exportDatabasesToDownload as exportDatabasesToDownloadVia,
    importDatabasesFromSession as importDatabasesFromSessionVia,
    stagedExportFile,
    triggerDownload,
    logger as sqliteLogger,
    SqliteWasmLogLevel,
} from '@sqlitewasmblazor/worker-common';

import {
    packArrayHeader,
    packBinHeader,
    packStr,
    packUint,
} from '@sqlitewasmblazor/worker-common';

/**
 * IMemoryView interface from dotnet runtime — view over managed Span/ArraySegment.
 */
interface IMemoryView {
    slice(): Uint8Array;
    slice(start: number): Uint8Array;
    slice(start: number, end: number): Uint8Array;
    // Write side of the dotnet MemoryView — copies `source` into the managed
    // Span this view wraps. Only valid while the originating synchronous
    // JSImport call is on the stack (the Span is unpinned on return).
    set(source: Uint8Array, targetOffset?: number): void;
    readonly byteLength: number;
}

const MODULE_NAME = 'Worker Bridge';

let worker: Worker | null = null;

/**
 * Streaming-response router — the half of the protocol whose payload rides
 * an OPFS staging file rather than postMessage. Export operations never send
 * bytes back: the worker writes into a staging file and streamDone carries
 * its name (plus a per-file offset table for the pool export), which the
 * bridge lifts as a disk-backed File. Shared with the plane-1 bridge; see
 * worker-common's stream-bridge.ts.
 */
const streams = createStreamRouter((message, transfer) => {
    if (!worker) {
        throw new Error('Worker not initialized');
    }
    worker.postMessage(message, transfer ?? []);
});

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
            sqliteLogger.info(MODULE_NAME, 'Worker ready');
            try {
                const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");
                exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerReady();
            } catch (error) {
                sqliteLogger.error(MODULE_NAME, 'Failed to call OnWorkerReady:', error);
            }
            return;
        }

        if (event.data.type === 'error') {
            sqliteLogger.error(MODULE_NAME, 'Worker error:', event.data.error);
            try {
                const exports = await (globalThis as any).getDotnetRuntime(0).getAssemblyExports("SqliteWasmBlazor.dll");
                exports.SqliteWasmBlazor.SqliteWasmWorkerBridge.OnWorkerError(event.data.error || 'Unknown worker error');
            } catch (error) {
                sqliteLogger.error(MODULE_NAME, 'Failed to call OnWorkerError:', error);
            }
            return;
        }

        // Streaming responses — keyed by `streamId` and settled JS-side.
        // C# never sees these messages.
        if (streams.dispatch(event.data)) {
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
                sqliteLogger.error(MODULE_NAME, 'Failed to call C# callback:', error);
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
        sqliteLogger.error(MODULE_NAME, 'Worker error event:', error);
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

export const logger = {
    /**
     * Two halves to configure: this module and the streaming router run on the
     * main thread, the SQLite modules run inside the worker. Each side holds
     * its own logger instance, so both need the level.
     *
     * `commandSql` is deliberately separate from the level: the level decides
     * how verbose logging is, this decides whether query *content* — SQL text,
     * parameter values — may be emitted at all. It mirrors
     * SqliteWasmOptions.EnableCommandSqlLogging.
     *
     * `tracing` is separate again: benchmarking output (per-statement timing)
     * that must be usable in a Release build without the rest, and that carries
     * durations only. Mirrors SqliteWasmOptions.EnableRequestTracing.
     */
    configureLogging(level: number, commandSql: boolean, tracing: boolean): void {
        sqliteLogger.setLogLevel(level as SqliteWasmLogLevel);
        if (!worker) {
            sqliteLogger.warn(MODULE_NAME, 'Worker not initialized, cannot configure logging');
            return;
        }
        worker.postMessage({
            type: 'configureLogging',
            level: level,
            commandSql: commandSql,
            tracing: tracing
        });
    }
};

// ---------------------------------------------------------------------------
// Chunked encrypted-disk export — worker → OPFS staging → File download.
//
// The worker rekeys every DB and writes the ciphertext contiguously into
// an OPFS staging file, then reports the staging name plus a per-file
// { name, offset, size } table via streamDone. The bridge lifts the
// staging file as a disk-backed File and composes the final v3 envelope
// as a virtual-concat Blob of:
//
//   <MessagePack header bytes (array(8) + 7 small fields)>
//   <per-file array headers + name + bin headers + File.slice() parts>
//
// The ciphertext never occupies main-thread memory — File.slice() parts
// stay disk-backed, which is what keeps iPhone Safari alive on large
// exports (Blobs constructed from ArrayBuffers are memory-backed in
// WebKit). C# never sees a managed byte[] of the envelope. The download
// fires from the bridge via anchor click; C# awaits a boolean result.
// ---------------------------------------------------------------------------

/**
 * JSImport entry point — called by C# `ExportPoolToDownloadAsync`. Drives
 * the worker staging export, composes the envelope Blob, triggers the
 * download. Returns `true` on completion. The staging file backing the
 * download is collected by the worker's init sweep next session.
 */
export function exportPoolToDownload(
    filename: string,
    metadataJson: string,
    kWrapView: IMemoryView,
): Promise<boolean> {
    return _assembleEnvelopeStaged(metadataJson, kWrapView).then(({ blob }) => {
        triggerDownload(filename, blob);
        return true;
    });
}

// ---------------------------------------------------------------------------
// Test/diagnostic export-to-bytes — assembles the identical v3 envelope Blob
// as {@link exportPoolToDownload}, then makes the bytes drainable by C#
// instead of triggering the anchor-click download. The whole envelope is
// materialised in memory (the very thing the streaming download path avoids),
// so this exists ONLY so in-page round-trip tests can feed a real export back
// into the guided import. `Task<byte[]>` is not a supported JS-interop return
// shape, so the protocol is: stash bytes → return length → C# reads chunks
// into a MemoryView → discard. See the C# JSImports
// `SqliteWasmWorkerBridge.{ExportPoolToBytesSessionAsync,ReadExportBytes,DiscardExportBytes}`.
// ---------------------------------------------------------------------------

const exportByteStash = new Map<number, Uint8Array>();

export function exportPoolToBytesSession(
    metadataJson: string,
    kWrapView: IMemoryView,
    sessionId: number,
): Promise<number> {
    if (exportByteStash.has(sessionId)) {
        return Promise.reject(new Error(
            `exportPoolToBytesSession: sessionId ${sessionId} already in use`));
    }
    return _assembleEnvelopeStaged(metadataJson, kWrapView)
        .then(async ({ blob, stagingFile }) => {
            const buf = await blob.arrayBuffer();
            // Bytes fully materialised — the staging file is no longer
            // referenced and can be deleted immediately.
            await deleteStagedExportFile(stagingFile);
            const bytes = new Uint8Array(buf);
            exportByteStash.set(sessionId, bytes);
            return bytes.length;
        });
}

export function readExportBytes(
    sessionId: number,
    offset: number,
    destView: IMemoryView,
): number {
    const bytes = exportByteStash.get(sessionId);
    if (!bytes) {
        throw new Error(`readExportBytes: unknown sessionId ${sessionId}`);
    }
    const remaining = bytes.length - offset;
    if (remaining <= 0) {
        return 0;
    }
    const n = Math.min(destView.byteLength, remaining);
    destView.set(bytes.subarray(offset, offset + n));
    return n;
}

export function discardExportBytes(sessionId: number): void {
    exportByteStash.delete(sessionId);
}

/**
 * Shared assembly path: drive the worker staging export, lift the staging
 * file as a disk-backed File, compose the MessagePack envelope Blob from
 * header bytes plus per-DB File.slice() segments via the positional
 * encoder. The returned staging file name lets the test-only bytes path
 * delete the staging entry once it has materialised the bytes.
 */
function _assembleEnvelopeStaged(
    metadataJson: string,
    kWrapView: IMemoryView,
): Promise<{ blob: Blob; stagingFile: string }> {
    const meta = JSON.parse(metadataJson) as {
        version: number;
        aadVersion: string;
        prfSaltBase64: string;
        ephemeralPublicKey: string;
        wrappedContentKeyCiphertext: string;
        wrappedContentKeyNonce: string;
        credentialIdHint: string;
    };

    return streams.request(
        (streamId) => {
            // Transfer K_wrap into the worker — the buffer detaches from the
            // main side immediately, matching the existing sendBinaryToWorker
            // ownership semantics. `data: { type }` matches the legacy
            // WorkerRequest shape the worker's onmessage destructures.
            const kWrap = kWrapView.slice();
            return {
                message: {
                    streamId,
                    data: {type: 'exportPoolToStaging'},
                    binaryPayload: kWrap.buffer,
                },
                transfer: [kWrap.buffer],
            };
        },
        async ({stagingFile, files}) => {
            if (stagingFile === undefined || files === undefined) {
                throw new Error(
                    'exportPoolToStaging: streamDone missing stagingFile/files');
            }
            const file = await stagedExportFile(stagingFile);
            // File.slice() segments stay backed by the OPFS entry —
            // composing them into the envelope Blob adds no main-thread
            // memory.
            const fileParts = files.map((f) => ({
                name: f.name,
                size: f.size,
                blob: file.slice(f.offset, f.offset + f.size),
            }));
            return {blob: composeEnvelopeBlob(meta, fileParts), stagingFile};
        });
}

/**
 * Compose the v3 EncryptedPoolEnvelope wire shape as a Blob — small
 * positional header bytes + per-DB Blob parts. Wire layout matches the
 * MessagePack-CSharp [Key(N)] positional record decoded by
 * <c>ImportPoolGuidedFromStreamAsync</c>:
 *
 *   [0] Version (uint)
 *   [1] AadVersion (str)
 *   [2] PrfSalt (bin, 32 bytes)
 *   [3] EphemeralPublicKey (str, Base64)
 *   [4] WrappedContentKeyCiphertext (str, Base64)
 *   [5] WrappedContentKeyNonce (str, Base64)
 *   [6] CredentialIdHint (str, Base64)
 *   [7] Files (array of [name(str), bytes(bin)])
 *
 * Returns a virtual-concatenation Blob — the per-DB segments are
 * File.slice() parts backed by the OPFS staging entry, so the payload
 * bytes never occupy main-thread memory.
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
    const decodedSalt = base64ToBytes(meta.prfSaltBase64);
    if (decodedSalt.length !== 32) {
        throw new Error(
            `composeEnvelopeBlob: prfSalt must decode to 32 bytes (got ${decodedSalt.length})`);
    }
    // Push the ArrayBuffer itself (always a valid BlobPart) so we don't
    // fight TS's `Uint8Array<ArrayBufferLike>` vs `Uint8Array<ArrayBuffer>`
    // distinction that BlobPart enforces in TS 5.x.
    const prfBuf = new ArrayBuffer(decodedSalt.length);
    new Uint8Array(prfBuf).set(decodedSalt);
    const parts: BlobPart[] = [];
    parts.push(packArrayHeader(8));
    parts.push(packUint(meta.version));
    parts.push(...packStr(meta.aadVersion));
    parts.push(packBinHeader(decodedSalt.length));
    parts.push(prfBuf);
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

// ---------------------------------------------------------------------------
// Chunked encrypted-disk import — C# → worker import session → worker.
//
// The picked file's bytes went straight into the worker, chunk by chunk,
// and are staged in an OPFS file there. Nothing about them passes through
// the main thread: these entries carry only the session id, and the
// worker lifts its own staged File per pass. Blobs built from
// ArrayBuffers are memory-backed in WebKit, which is what closed the
// pool's access handles mid-import on iOS when this glue composed one.
//
// Two-pass: preflight authenticates the envelope before the caller wipes
// anything, commit rewrites it under the new key. Each pass streams the
// staged file afresh; the session stays open until C# discards it.
// ---------------------------------------------------------------------------

/** JSImport entry — preflight: AEAD-verify slot 0 of every file under K_wrap. */
export function importPoolStreamPreflightFromSession(
    sessionId: number,
    kWrapView: IMemoryView,
): Promise<number> {
    return _sendImportPoolStreamSession(
        'importPoolStreamPreflight', sessionId, kWrapView);
}

/** JSImport entry — commit: re-stream the envelope, decrypt under K_wrap, re-encrypt under globalKey. */
export function importPoolStreamCommitFromSession(
    sessionId: number,
    kWrapView: IMemoryView,
): Promise<number> {
    return _sendImportPoolStreamSession(
        'importPoolStreamCommit', sessionId, kWrapView);
}

/**
 * JSImport entry — multi-DB plain export. One-line adapter over the shared
 * implementation; only the router (and so the worker behind it) differs
 * from plane 1.
 */
export function exportDatabasesToDownload(
    filename: string,
    dbNamesJson: string,
): Promise<boolean> {
    return exportDatabasesToDownloadVia(streams, filename, dbNamesJson);
}

/** JSImport entry — multi-DB plain import from a staged `.dbs` envelope. */
export function importDatabasesFromSession(
    sessionId: number,
    keepExisting: boolean,
): Promise<number> {
    return importDatabasesFromSessionVia(streams, sessionId, keepExisting);
}

function _sendImportPoolStreamSession(
    type: 'importPoolStreamPreflight' | 'importPoolStreamCommit',
    sessionId: number,
    kWrapView: IMemoryView,
): Promise<number> {
    return streams.request(
        (streamId) => {
            const kWrap = kWrapView.slice();
            return {
                message: {streamId, data: {type, sessionId}, binaryPayload: kWrap.buffer},
                transfer: [kWrap.buffer],
            };
        },
        ({result}) => {
            if (typeof result !== 'number') {
                throw new Error(`${type} streamDone missing result`);
            }
            return result;
        });
}

// Staged export downloads live in worker-common so both bridges share one
// staging-directory name and one filename → content-type mapping; see
// staged-download.ts for why the content type decides whether iOS Safari
// keeps the filename we ask for.
export { downloadStagedExport };

(globalThis as any).sqliteWasmWorker = {
    initializeBridge,
    sendToWorker,
    sendBinaryToWorker,
    exportPoolToDownload,
    exportPoolToBytesSession,
    readExportBytes,
    discardExportBytes,
    importPoolStreamPreflightFromSession,
    importPoolStreamCommitFromSession,
    importDatabasesFromSession,
    exportDatabasesToDownload,
    downloadStagedExport,
};

(globalThis as any).__sqliteWasmLogger = logger;
