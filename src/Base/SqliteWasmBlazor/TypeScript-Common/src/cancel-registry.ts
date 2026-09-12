// cancel-registry.ts
// Service-worker half of query cancellation: the requests the page has
// cancelled, answered to the SQLite worker's polls.
//
// The SQLite worker cannot be told directly. It is single-threaded, and while
// a statement runs nothing else runs there, so a posted message waits until
// the statement ends — which is the thing being cancelled. What it can do
// from inside the statement is block on a synchronous XMLHttpRequest, and a
// service worker can answer that request without touching the network. So
// the page posts "cancel (session, id)" here, the worker asks "is (session,
// id) cancelled?" here, and the two never have to meet.
//
// Everything is in memory. A service worker lives at least as long as it is
// handling events, and a poll arrives within the poll interval of the cancel
// it answers; an idle-terminated worker loses only entries whose requests
// finished long ago.

import {isCancelMessage, parseCancelPollUrl} from './cancel-protocol.js';

/**
 * Entries older than this are dropped when the next one is added. A request
 * that old has finished on its own; nothing is left to interrupt.
 */
export const CANCEL_ENTRY_TTL_MS = 60_000;

export interface CancelRegistry {
    cancel(session: string, id: number, now?: number): void;
    isCancelled(session: string, id: number): boolean;
    readonly size: number;
}

export function createCancelRegistry(ttlMs: number = CANCEL_ENTRY_TTL_MS): CancelRegistry {
    const cancelledAt = new Map<string, number>();
    const keyOf = (session: string, id: number) => `${session}/${id}`;

    return {
        cancel(session, id, now = Date.now()) {
            for (const [key, at] of cancelledAt) {
                if (now - at > ttlMs) {
                    cancelledAt.delete(key);
                }
            }
            cancelledAt.set(keyOf(session, id), now);
        },
        isCancelled(session, id) {
            return cancelledAt.has(keyOf(session, id));
        },
        get size() {
            return cancelledAt.size;
        },
    };
}

/**
 * The function a host's service worker calls from its own `fetch` and
 * `message` listeners. Claims the event — answers the poll, or records the
 * cancel — when it belongs to this protocol and returns true; returns false
 * without touching anything else, so the host's own handling runs as before.
 */
export function createCancelEventHandler(
    registry: CancelRegistry = createCancelRegistry(),
): (event: Event) => boolean {
    return (event) => {
        if (event.type === 'fetch') {
            const fetchEvent = event as FetchEvent;
            if (fetchEvent.request.method !== 'GET') {
                return false;
            }
            const target = parseCancelPollUrl(fetchEvent.request.url);
            if (target === null) {
                return false;
            }
            const cancelled = registry.isCancelled(target.session, target.id);
            fetchEvent.respondWith(new Response(null, {
                status: cancelled ? 204 : 404,
                headers: {'Cache-Control': 'no-store'},
            }));
            return true;
        }

        if (event.type === 'message') {
            const data: unknown = (event as MessageEvent).data;
            if (!isCancelMessage(data)) {
                return false;
            }
            registry.cancel(data.session, data.id);
            return true;
        }

        return false;
    };
}
