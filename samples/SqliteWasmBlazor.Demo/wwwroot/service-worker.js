// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
//
// Query cancellation still needs a service worker to carry the cancel from the
// page to the SQLite worker, so the registry rides along here too: one
// importScripts, and both listeners hand it every event and keep nothing.
self.importScripts('_content/SqliteWasmBlazor/sqlite-wasm-cancel.sw.js');

self.addEventListener('fetch', event => {
    handleSqliteWasmCancel(event);
});
self.addEventListener('message', event => {
    handleSqliteWasmCancel(event);
});
