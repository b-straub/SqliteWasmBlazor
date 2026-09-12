// cancel-bridge.ts
// Page half of query cancellation. Decides, when the bridge starts, whether
// a service worker controls this page; mints the session token the worker
// polls under; and posts a cancel to the controller when C# stops waiting
// for a request.
//
// The decision is made once. A service worker takes control of the page
// that registered it only on the next navigation, so the very first session
// of a fresh install runs without cancellation — the bridge says so, and
// the next load has it.

import {CANCEL_MESSAGE_TYPE, type CancelMessage} from './cancel-protocol.js';

export interface CancellationChannel {
    /** The token requests are polled under; null when no service worker controls the page. */
    readonly session: string | null;
    /** Tell the service worker that `id` is cancelled. No-op without a controller. */
    cancel(id: number): void;
}

export function openCancellationChannel(): CancellationChannel {
    const container = navigator.serviceWorker;
    if (container === undefined || container.controller === null) {
        return {session: null, cancel() {}};
    }

    const session = newSessionToken();
    return {
        session,
        cancel(id) {
            // Read at post time, not captured: an updated service worker
            // replaces the controller, and a message to the old one is lost.
            const controller = navigator.serviceWorker.controller;
            if (controller === null) {
                return;
            }
            const message: CancelMessage = {type: CANCEL_MESSAGE_TYPE, session, id};
            controller.postMessage(message);
        },
    };
}

/**
 * Sixteen random bytes as hex. Uniqueness across tabs and reloads is all
 * that is asked of it; it is an identifier, not a secret.
 */
function newSessionToken(): string {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    let token = '';
    for (const byte of bytes) {
        token += byte.toString(16).padStart(2, '0');
    }
    return token;
}
