// sqlite-wasm-cancel.sw.ts
// Service-worker entry for query cancellation. Ships as
// `_content/SqliteWasmBlazor/sqlite-wasm-cancel.sw.js`. A host's service
// worker pulls it in with `importScripts` and delegates from its own
// listeners:
//
//   self.importScripts('_content/SqliteWasmBlazor/sqlite-wasm-cancel.sw.js');
//   self.addEventListener('fetch', event => {
//       if (handleSqliteWasmCancel(event)) { return; }
//       // the host's own fetch handling
//   });
//   self.addEventListener('message', event => {
//       if (handleSqliteWasmCancel(event)) { return; }
//       // the host's own message handling
//   });
//
// A classic script, because importScripts takes nothing else, so it defines
// exactly one global and registers no listeners of its own. The host's
// service worker stays the host's.

import {createCancelEventHandler} from '@sqlitewasmblazor/worker-common/cancel-registry';

declare const self: ServiceWorkerGlobalScope & {
    handleSqliteWasmCancel: (event: Event) => boolean;
};

self.handleSqliteWasmCancel = createCancelEventHandler();
