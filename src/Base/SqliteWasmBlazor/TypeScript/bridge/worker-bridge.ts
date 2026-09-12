// worker-bridge.ts
// Bridge between C# JSImport and Web Worker.
// Exposes a single async initializeBridge(baseHref, assetRoot) entry point;
// C# awaits its returned Promise so worker creation errors surface on the .NET side.

import {
    createStreamRouter,
    downloadStagedExport,
    exportDatabasesToDownload as exportDatabasesToDownloadVia,
    importDatabasesFromSession as importDatabasesFromSessionVia,
    logger as sqliteLogger,
    SqliteWasmLogLevel,
    openCancellationChannel,
    type CancellationChannel,
} from '@sqlitewasmblazor/worker-common';

/**
 * IMemoryView interface from dotnet runtime — view over managed Span/ArraySegment.
 */
interface IMemoryView {
    slice(): Uint8Array;
    slice(start: number): Uint8Array;
    slice(start: number, end: number): Uint8Array;
}

const MODULE_NAME = 'Worker Bridge';

let worker: Worker | null = null;

/**
 * Query cancellation — opened once at initializeBridge. Its session is null
 * when no service worker controls the page, and the worker is told so in
 * the init message; C# reads the same fact through canCancelQueries.
 */
let cancellation: CancellationChannel = {session: null, cancel() {}};

/**
 * Streaming-response router — the half of the protocol whose payload rides
 * an OPFS staging file instead of postMessage. Lives in worker-common so
 * both bridges route the same way; see stream-bridge.ts.
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

    cancellation = openCancellationChannel();
    worker.postMessage({ type: 'init', baseHref, assetRoot, cancelSession: cancellation.session });

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

/** Whether a cancel posted from C# can reach a running statement. */
export function canCancelQueries(): boolean {
    return cancellation.session !== null;
}

/**
 * C# stopped waiting for request `id`. Tells the service worker, which
 * the SQLite worker's progress handler polls; see cancel-poll.ts.
 */
export function cancelRequest(id: number): void {
    cancellation.cancel(id);
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

/**
 * JSImport entry — multi-database plain export to a `.dbs` download. Both
 * this and the import below are one-line adapters over the shared
 * implementation; the router is the only per-bridge part.
 */
export function exportDatabasesToDownload(
    filename: string,
    dbNamesJson: string,
): Promise<boolean> {
    return exportDatabasesToDownloadVia(streams, filename, dbNamesJson);
}

/** JSImport entry — multi-database plain import from a staged `.dbs` envelope. */
export function importDatabasesFromSession(
    sessionId: number,
    keepExisting: boolean,
): Promise<number> {
    return importDatabasesFromSessionVia(streams, sessionId, keepExisting);
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
    exportDatabasesToDownload,
    importDatabasesFromSession,
    downloadStagedExport,
    canCancelQueries,
    cancelRequest,
};

(globalThis as any).__sqliteWasmLogger = logger;
