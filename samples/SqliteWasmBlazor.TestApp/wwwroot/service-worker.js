// service-worker.js — the TestApp's service worker.
//
// Caches nothing. It exists so query cancellation has a registry to keep:
// the SQLite worker polls this worker from inside a running statement, and
// the page posts cancels here. One importScripts is the whole integration;
// the two listeners hand it every event and keep nothing for themselves.
//
// skipWaiting + clients.claim take control of the page that registered it
// during that same load. A real app registers once and is controlled from
// the next navigation on; the test harness loads the page once per run.

self.importScripts('_content/SqliteWasmBlazor/sqlite-wasm-cancel.sw.js');

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
    handleSqliteWasmCancel(event);
});
self.addEventListener('message', event => {
    handleSqliteWasmCancel(event);
});
