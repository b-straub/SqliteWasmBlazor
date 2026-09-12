# Real query cancellation: progress handler + service-worker answered sync XHR

Cancellation never reaches the worker. `SqliteWasmWorkerBridge.SendRequestAsync`
honours a cancelled token by dropping the pending `TaskCompletionSource` — the
caller stops waiting, the worker does not stop working. The protocol has no
cancel message, and could not act on one if it had: the worker is
single-threaded and `db.exec` is a synchronous `for (; stmt.step(); …)` loop
that never yields, so a message posted to it sits in the queue until the query
ends on its own.

Measured worst case, encrypted FTS on the Demo: `fetch#9 served 1290 rows in
56954 ms` where its own work was 65 ms — the rest was queueing behind eight
abandoned predecessors from earlier keystrokes.

## Why B

| | SharedArrayBuffer + `Atomics.wait` | **Progress handler + sync XHR to a service worker** | Steppable exec (yield between rows) |
| --- | --- | --- | --- |
| Interrupts a running statement | yes | yes | only between rows — a big `ORDER BY rank` sort is one step |
| Needs COOP/COEP | **yes** — breaks any embed, third-party asset, or PWA that cannot set the headers | no | no |
| Needs a service worker | no | yes — the target apps are PWAs and have one | no |
| Changes the execution model | no | no | yes: per-row await on every query, ~ms each |

B was chosen because it interrupts inside a statement without cross-origin
isolation. Steppable exec is not an alternative for the slow case (a single
step that sorts 267k matches) but would still be worth having for row
streaming later; it is out of scope here.

## How it works

1. The C# side gives every request an id it already has, and on token
   cancellation tells the **service worker** — not the SQLite worker — "request
   N is cancelled". The service worker keeps that in memory.
2. Before executing a request, the SQLite worker installs
   `sqlite3_progress_handler(db, N, cb)`. SQLite calls `cb` every N VM ops.
3. `cb` asks the service worker whether the current request is cancelled,
   over a **synchronous `XMLHttpRequest`** to a URL the service worker
   intercepts. Sync XHR is deprecated on the main thread and fully supported
   in workers; blocking is the point. Rate-limited by wall clock, so the poll
   costs a handful of round-trips per second, not one per N ops.
4. If cancelled, `cb` returns non-zero. SQLite aborts the statement with
   `SQLITE_INTERRUPT`; the worker reports it; C# maps it to
   `OperationCanceledException`. The worker moves to the next request at once.

Nothing changes for a host without a service worker: the bridge detects at
startup that no controller is present and reports that cancellation is
unavailable — once, as a fact, not silently.

## Status

| Goal | State | Commit |
| --- | --- | --- |
| G1 service-worker registry | done | (this commit) |
| G2 worker progress handler | next | |
| G3 bridge + `CanCancelQueries` + TestApp cases | open | |
| G4 Demo service worker | open | |
| G5 docs + CHANGELOG | open | |

Decisions taken while building G1, on top of the plan:

- Requests are named `(session, id)`, not `id` alone. Ids restart at 1 on
  every page load and are private to a tab, so the registry would otherwise
  interrupt tab B's request 5 because tab A cancelled its own. The bridge
  mints a random session token when it starts; the SW script never sees it
  as anything but an opaque key.
- The SW-side logic is `worker-common/cancel-registry.ts` (pure, Vitest-
  covered) behind a subpath export; `TypeScript/sw/sqlite-wasm-cancel.sw.ts`
  is the three-line entry that binds `self.handleSqliteWasmCancel`. Importing
  the registry through worker-common's index would drag msgpackr and the
  worker state into a service worker; the subpath keeps the bundle at ~1 KB.

## Goal tree

### G1 — Service worker side: the cancel registry
- `src/Base/SqliteWasmBlazor/TypeScript-Common/src/cancel-registry.ts` →
  ships as `sqlite-wasm-cancel.sw.js` static web asset. Exposes one function
  the host's service worker calls from its own `fetch`/`message` handlers:
  `handleSqliteWasmCancel(event): boolean` — claims the event if the URL is
  `/_sqlite-wasm/cancel/<id>` (GET → `204` cancelled / `404` not) or the
  message is `{type:'sqlite-wasm-cancel', id}`. `importScripts` is the
  integration; the host's SW stays the host's.
- Registry is a `Set<number>` with a bounded age (entries older than a minute
  are dropped; a request that old finished long ago).
- Verify: a Vitest suite over the handler with a fake `FetchEvent`.

### G2 — SQLite worker: the progress handler
- Around `executeSql` / every request that runs a statement: set
  `currentRequestId`, install the handler with N = 10 000 ops, clear both after.
- Handler body: if `now - lastPoll < 50 ms` return 0; else sync XHR to
  `/_sqlite-wasm/cancel/<currentRequestId>`, cache the answer, return 1 on 204.
- `sqlite3_progress_handler` is on the C API; confirm `@sqlite.org/sqlite-wasm`
  3.53 exposes `sqlite3.capi.sqlite3_progress_handler` with a JS callback —
  it does for `sqlite3_exec` callbacks, check the same wrapper covers this.
- The handler must survive the crypto plane: both `sqlite-worker.ts` files, or
  the shared piece in `worker-common`.
- Verify: TestApp case `Cancellation_InterruptsRunningStatement` — a `SELECT
  COUNT(*) FROM big a CROSS JOIN big b` cancelled after 100 ms must throw
  `OperationCanceledException` within 300 ms **and** a trivial query issued
  right after must complete within 100 ms (the worker was freed, not merely
  abandoned).

### G3 — Bridge: wire the token
- `SendRequestAsync`: on cancellation, post `{type:'sqlite-wasm-cancel', id}`
  to `navigator.serviceWorker.controller` via a new `[JSImport]`, *and* keep
  dropping the TCS as today.
- Map a worker response carrying `SQLITE_INTERRUPT` to
  `OperationCanceledException` bound to the caller's token.
- Detect capability once at `InitializeAsync`: `controller !== null`. Expose
  `ISqliteWasmDatabaseService.CanCancelQueries`. Log the outcome at
  Information through `SqliteWasmLogger`.
- Verify: same TestApp case, plus `Cancellation_UnavailableWithoutServiceWorker`
  on the plain plane if the harness runs without a SW there (it does today) —
  asserts `CanCancelQueries == false` and that cancellation degrades to
  drop-the-response, not to a hang.

### G4 — Demo: register the handler in the existing service worker
- `samples/SqliteWasmBlazor.Demo/wwwroot/service-worker.js` (and the
  published variant) gains `importScripts('_content/SqliteWasmBlazor/sqlite-wasm-cancel.sw.js')`
  and delegates. Mind [[project_demo_index_html_sw_form]] and the SW
  placeholder swap.
- The TodoList search is the acceptance test: type `1`, `12`, `123`, `1234`
  quickly on the 4M-row encrypted seed — the last result must arrive in ~its
  own cost, not after its predecessors drain.

### G5 — Docs + CHANGELOG
- `docs/advanced-features.md`: a "Cancelling queries" section — what is
  needed (a SW, one `importScripts`), what happens without.
- CHANGELOG entry; note the first-load caveat below.

## Risks and known edges

- **First load.** A service worker does not control the page that registered
  it until the next navigation. Cancellation is unavailable during the very
  first session of a fresh install; `CanCancelQueries` says so.
- **Scope.** The SQLite worker's URL must be inside the SW's scope, or its
  fetches are not intercepted. `_content/SqliteWasmBlazor/` under the app
  root is; a host serving the worker from elsewhere must widen the scope.
- **Sync XHR cost.** Each poll blocks the worker for a SW round-trip, typically
  ~1 ms. The 50 ms rate limit bounds it to ~2% of a long query. Tune N and the
  interval against the 56 s case before locking the numbers.
- **iOS.** Sync XHR inside a worker and SW fetch interception both work in
  WebKit; the combination inside a PWA needs a run on the iPad, same as every
  other worker change ([[project_ios_large_import_failure]]).
- **Interrupt inside a write.** `SQLITE_INTERRUPT` rolls the statement back;
  a cancelled `SaveChanges` must surface as cancelled, not as partially
  applied. `SqliteWasmTransaction.DisposeAsync` (fixed `1ee7c50`) is what
  makes the rollback actually reach the worker.

## Not in scope

- SharedArrayBuffer path as an optional fast lane for hosts that already set
  COOP/COEP. Possible later; the SW path is the one that has to exist.
- Steppable exec for row streaming.
