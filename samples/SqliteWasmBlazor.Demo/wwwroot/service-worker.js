// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
//
// It is registered in development all the same: query cancellation needs a
// service worker to carry the cancel from the page to the SQLite worker, so
// the registry rides along here — one importScripts, and both listeners hand
// it every event and keep nothing for themselves.
self.importScripts('_content/SqliteWasmBlazor/sqlite-wasm-cancel.sw.js');

self.addEventListener('fetch', event => {
    handleSqliteWasmCancel(event);
});
self.addEventListener('message', event => {
    handleSqliteWasmCancel(event);
});
