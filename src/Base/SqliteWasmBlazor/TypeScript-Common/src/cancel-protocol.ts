// cancel-protocol.ts
// The contract the three parties of query cancellation share: the page,
// which posts a cancel; the service worker, which keeps the registry; and
// the SQLite worker, which polls it from inside a running statement.
//
// A request is named by (session, id). The id is the bridge's request
// counter — it restarts at 1 on every page load and is private to one tab —
// so a tab-local token is needed to keep another tab's request 5, or this
// tab's request 5 from before a reload, from matching this one. The bridge
// mints the session when it starts; nothing else ever reads it.

/** `postMessage` type the page sends to the service worker controlling it. */
export const CANCEL_MESSAGE_TYPE = 'sqlite-wasm-cancel';

/** Path, under the app's base href, that the service worker answers. */
export const CANCEL_PATH = '_sqlite-wasm/cancel/';

/**
 * Header on every answer the registry gives. A poll that comes back without
 * it reached the network instead — the page is controlled but the worker's
 * requests are not intercepted — and the worker stops polling.
 */
export const CANCEL_ANSWER_HEADER = 'X-SqliteWasm-Cancel';

export interface CancelMessage {
    type: typeof CANCEL_MESSAGE_TYPE;
    session: string;
    id: number;
}

export interface CancelTarget {
    session: string;
    id: number;
}

export function isCancelMessage(data: unknown): data is CancelMessage {
    if (typeof data !== 'object' || data === null) {
        return false;
    }
    const message = data as Record<string, unknown>;
    return message.type === CANCEL_MESSAGE_TYPE
        && typeof message.session === 'string'
        && typeof message.id === 'number';
}

/** URL the SQLite worker polls for one request. */
export function cancelPollUrl(baseHref: string, target: CancelTarget): string {
    return `${baseHref}${CANCEL_PATH}${target.session}/${target.id}`;
}

const POLL_PATH_PATTERN = new RegExp(`/${CANCEL_PATH}([A-Za-z0-9_-]+)/(\\d+)$`);

/** The request a poll URL names, or null when the URL is not a poll. */
export function parseCancelPollUrl(url: string): CancelTarget | null {
    const match = POLL_PATH_PATTERN.exec(new URL(url).pathname);
    if (match === null) {
        return null;
    }
    return {session: match[1], id: Number(match[2])};
}
