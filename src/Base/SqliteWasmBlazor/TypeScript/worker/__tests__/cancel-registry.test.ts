// cancel-registry.test.ts
//
// The service-worker side of query cancellation, driven with fake events.
// The contract under test: a cancel message lands in the registry, a poll
// for a cancelled request gets 204, every other poll gets 404, and nothing
// outside the protocol is touched — the host's own fetch handling has to
// keep working around it.

import {describe, expect, it} from 'vitest';
import {
    CANCEL_MESSAGE_TYPE,
    cancelPollUrl,
    isCancelMessage,
    parseCancelPollUrl,
} from '@sqlitewasmblazor/worker-common';
import {
    CANCEL_ENTRY_TTL_MS,
    createCancelEventHandler,
    createCancelRegistry,
} from '@sqlitewasmblazor/worker-common/cancel-registry';

const ORIGIN = 'https://app.example';

interface FakeFetchEvent {
    type: 'fetch';
    request: { url: string; method: string };
    response: Promise<Response> | null;
    respondWith(response: Promise<Response> | Response): void;
}

function fetchEvent(url: string, method = 'GET'): FakeFetchEvent {
    return {
        type: 'fetch',
        request: {url, method},
        response: null,
        respondWith(response) {
            this.response = Promise.resolve(response);
        },
    };
}

function messageEvent(data: unknown): { type: 'message'; data: unknown } {
    return {type: 'message', data};
}

const asEvent = (event: unknown) => event as Event;

describe('cancel protocol', () => {
    it('round-trips a poll URL under any base href', () => {
        for (const baseHref of ['/', '/myapp/', 'https://app.example/deep/er/']) {
            const url = new URL(cancelPollUrl(baseHref, {session: 'abc123', id: 42}), ORIGIN).href;
            expect(parseCancelPollUrl(url)).toEqual({session: 'abc123', id: 42});
        }
    });

    it('does not mistake other URLs for polls', () => {
        expect(parseCancelPollUrl(`${ORIGIN}/index.html`)).toBeNull();
        expect(parseCancelPollUrl(`${ORIGIN}/_sqlite-wasm/cancel/`)).toBeNull();
        expect(parseCancelPollUrl(`${ORIGIN}/_sqlite-wasm/cancel/abc/notanumber`)).toBeNull();
        expect(parseCancelPollUrl(`${ORIGIN}/_sqlite-wasm/cancel/abc/7/extra`)).toBeNull();
    });

    it('accepts only a well-formed cancel message', () => {
        expect(isCancelMessage({type: CANCEL_MESSAGE_TYPE, session: 's', id: 1})).toBe(true);
        expect(isCancelMessage({type: 'SKIP_WAITING'})).toBe(false);
        expect(isCancelMessage({type: CANCEL_MESSAGE_TYPE, session: 's', id: '1'})).toBe(false);
        expect(isCancelMessage({type: CANCEL_MESSAGE_TYPE, id: 1})).toBe(false);
        expect(isCancelMessage(null)).toBe(false);
        expect(isCancelMessage('sqlite-wasm-cancel')).toBe(false);
    });
});

describe('cancel registry', () => {
    it('knows only what it was told', () => {
        const registry = createCancelRegistry();
        registry.cancel('s1', 5);

        expect(registry.isCancelled('s1', 5)).toBe(true);
        expect(registry.isCancelled('s1', 6)).toBe(false);
        expect(registry.isCancelled('s2', 5)).toBe(false);
    });

    it('drops entries older than the TTL when a new one arrives', () => {
        const registry = createCancelRegistry();
        registry.cancel('s1', 1, 0);
        registry.cancel('s1', 2, CANCEL_ENTRY_TTL_MS);
        expect(registry.size).toBe(2);

        registry.cancel('s1', 3, CANCEL_ENTRY_TTL_MS + 1);
        expect(registry.isCancelled('s1', 1)).toBe(false);
        expect(registry.isCancelled('s1', 2)).toBe(true);
        expect(registry.isCancelled('s1', 3)).toBe(true);
        expect(registry.size).toBe(2);
    });
});

describe('cancel event handler', () => {
    const poll = (session: string, id: number) =>
        fetchEvent(new URL(cancelPollUrl('/', {session, id}), ORIGIN).href);

    it('answers 404 for a request nobody cancelled', async () => {
        const handle = createCancelEventHandler();
        const event = poll('s1', 1);

        expect(handle(asEvent(event))).toBe(true);
        expect((await event.response)?.status).toBe(404);
    });

    it('answers 204 after the cancel message for that request', async () => {
        const handle = createCancelEventHandler();

        expect(handle(asEvent(messageEvent({type: CANCEL_MESSAGE_TYPE, session: 's1', id: 1})))).toBe(true);

        const cancelled = poll('s1', 1);
        expect(handle(asEvent(cancelled))).toBe(true);
        expect((await cancelled.response)?.status).toBe(204);

        const other = poll('s1', 2);
        expect(handle(asEvent(other))).toBe(true);
        expect((await other.response)?.status).toBe(404);
    });

    it('keeps sessions apart', async () => {
        const handle = createCancelEventHandler();
        handle(asEvent(messageEvent({type: CANCEL_MESSAGE_TYPE, session: 'tab-a', id: 1})));

        const otherTab = poll('tab-b', 1);
        handle(asEvent(otherTab));
        expect((await otherTab.response)?.status).toBe(404);
    });

    it('leaves every other event to the host', () => {
        const handle = createCancelEventHandler();

        const page = fetchEvent(`${ORIGIN}/index.html`);
        expect(handle(asEvent(page))).toBe(false);
        expect(page.response).toBeNull();

        const post = fetchEvent(new URL(cancelPollUrl('/', {session: 's1', id: 1}), ORIGIN).href, 'POST');
        expect(handle(asEvent(post))).toBe(false);
        expect(post.response).toBeNull();

        expect(handle(asEvent(messageEvent({type: 'SKIP_WAITING'})))).toBe(false);
        expect(handle(asEvent({type: 'activate'}))).toBe(false);
    });

    it('marks its responses uncacheable', async () => {
        const handle = createCancelEventHandler();
        const event = poll('s1', 9);
        handle(asEvent(event));

        expect((await event.response)?.headers.get('Cache-Control')).toBe('no-store');
    });
});
