// cancel-poll.ts
// SQLite-worker half of query cancellation: a progress handler that asks the
// service worker, from inside a running statement, whether the request it is
// executing has been cancelled.
//
// While `db.exec` steps a statement nothing else runs on this thread, so a
// message cannot reach it — and a message is all the bridge has. What can
// reach it is a synchronous XMLHttpRequest: the thread blocks on it, the
// service worker controlling the page answers it, and the answer lands
// inside the callback SQLite invokes every PROGRESS_OPS virtual-machine
// instructions. A non-zero return from that callback aborts the statement
// with SQLITE_INTERRUPT.
//
// The poll is rate-limited by wall clock, not by ops. A round trip to the
// service worker costs about a millisecond, so one every POLL_INTERVAL_MS
// bounds the overhead at a few percent of a long statement and at nothing
// for a short one, which never reaches the first callback. The clock is
// shared across requests on purpose: a burst of cheap statements does not
// pay a poll each.
//
// Without a service worker there is nothing to ask, so the handler is never
// installed and every statement runs exactly as before.

import {CANCEL_ANSWER_HEADER, type CancelTarget, cancelPollUrl} from './cancel-protocol.js';
import {logger} from './sqlite-logger.js';
import {MODULE_NAME, sqlite3} from './worker-state.js';

/** Virtual-machine instructions between two progress callbacks. */
export const PROGRESS_OPS = 10_000;

/** Minimum wall-clock gap between two polls of the service worker. */
export const POLL_INTERVAL_MS = 50;

const SQLITE_INTERRUPT = 9;

let session: string | null = null;
let baseHref = '/';
let currentRequestId = 0;
let lastPollAt = 0;

/**
 * Configure from the bridge's init message. A null session means no service
 * worker controls the page; nothing is ever polled.
 */
export function configureCancellation(href: string, cancelSession: string | null): void {
    baseHref = href;
    session = cancelSession;
}

/** True when a cancel from the page can reach a running statement. */
export function isCancellationEnabled(): boolean {
    return session !== null;
}

/**
 * Run `statement` for `requestId` with the progress handler installed on
 * `db`. The id is held only for the synchronous span of the call — the
 * worker's message loop is async and interleaves at awaits, so module state
 * spanning an await would name the wrong request.
 *
 * The handler is installed on every call and never uninstalled: the sqlite
 * binding caches the function pointer per database, so reinstalling the same
 * function is a map lookup, while an uninstall/reinstall pair allocates. A
 * callback that fires outside a wrapped statement sees no request and
 * returns at once.
 */
export function runInterruptible<T>(db: { pointer: number }, requestId: number, statement: () => T): T {
    if (session === null) {
        return statement();
    }

    currentRequestId = requestId;
    sqlite3.capi.sqlite3_progress_handler(db.pointer, PROGRESS_OPS, onProgress, 0);
    try {
        return statement();
    } finally {
        currentRequestId = 0;
    }
}

/** True when `error` is the SQLite3Error a cancelled statement throws. */
export function isInterruptError(error: unknown): boolean {
    return typeof error === 'object'
        && error !== null
        && (error as { resultCode?: unknown }).resultCode === SQLITE_INTERRUPT;
}

function onProgress(): number {
    if (currentRequestId === 0 || session === null) {
        return 0;
    }

    const now = performance.now();
    if (now - lastPollAt < POLL_INTERVAL_MS) {
        return 0;
    }
    lastPollAt = now;

    return isCancelled({session, id: currentRequestId}) ? 1 : 0;
}

/**
 * One poll. An answer that did not come from the registry — the request
 * reached the network, or failed — means the service worker controls the
 * page but not this worker's fetches, and every further poll would be a
 * network round trip for nothing. Cancellation is switched off for the rest
 * of the session and the statement runs on as it would without it.
 */
function isCancelled(target: CancelTarget): boolean {
    const request = new XMLHttpRequest();
    request.open('GET', cancelPollUrl(baseHref, target), false);
    try {
        request.send();
    } catch (error) {
        disable('the poll failed', error);
        return false;
    }
    if (request.getResponseHeader(CANCEL_ANSWER_HEADER) === null) {
        disable(`the poll reached the network (HTTP ${request.status}) instead of the service worker`);
        return false;
    }
    return request.status === 204;
}

function disable(reason: string, error?: unknown): void {
    session = null;
    logger.warn(
        MODULE_NAME,
        `query cancellation disabled for this session: ${reason}. ` +
        'Is the SQLite worker inside the service worker\'s scope?',
        ...(error === undefined ? [] : [error]));
}
