# SqliteWasmBlazor — Plane 1: plain SQLite engine

A Blazor-WebAssembly SQLite host that runs SQL inside a dedicated Web
Worker (OPFS SAHPool VFS, `@sqlite.org/sqlite-wasm`) and exposes both an
ADO.NET surface (`SqliteWasmConnection` / `Command` / `Reader`) and an
EF Core provider.

Plane 1 is **plain SQLite only** — opt into PRF-keyed encryption by adding
**Plane 2** (`SqliteWasmBlazor.Crypto` engine + `SqliteWasmBlazor.Crypto.UI`
panels), and into multi-device encrypted sync by adding **Plane 3**
(`SqliteWasmBlazor.CryptoSync` engine + `SqliteWasmBlazor.CryptoSync.UI`
panels).

## Public registration

```csharp
// Program.cs
builder.Services.AddSqliteWasm();                       // bridge + worker
builder.Services.AddDbContextFactory<MyDbContext>(opt =>
    opt.UseSqliteWasm("Data Source=mydb.db"));

var host = builder.Build();
await host.Services.InitializeSqliteWasmDatabaseAsync<MyDbContext>(opt =>
{
    opt.BaseHref = builder.HostEnvironment.BaseAddress;
    opt.AssetRoot = "_content/SqliteWasmBlazor/";
});
await host.RunAsync();
```

## Host seams

Plane 1 has no required host seams — the worker plus options are enough.

## Plane invariants (locked by build-time tests)

- This assembly never references `SqliteWasmBlazor.Crypto.UI` or
  `SqliteWasmBlazor.CryptoSync.*`.
- After the plane-split pass lands (see ROADMAP), this assembly will also
  contain zero types whose namespace starts with `SqliteWasmBlazor.Crypto`
  — the Plane 2 engine moves to its own assembly.
- Layer-leak guard: `ThreePlaneLayerGuardTests` in the CryptoSync test
  suite fails the build on regression.

## What's in this directory

- `Services/SqliteWasmWorkerBridge.*.cs` — C# ↔ Web-Worker bridge (partial
  class split by concern: core dispatch / `.Encryption.cs` / `.Persistence.cs`
  / `.Delta.cs`).
- `Ado/` — ADO.NET provider (connection / command / reader / parameter).
- `Extensions/` — `AddSqliteWasm` + the `InitializeSqliteWasm*Async`
  startup helpers.
- `Crypto/` — **Plane 2 engine, currently colocated.** The encryption
  services consumed by `Crypto.UI` live here for build-history reasons;
  the plane-split pass will carve them out into `SqliteWasmBlazor.Crypto`
  (see ROADMAP). Plain-engine consumers using only `AddSqliteWasm()`
  never activate this code path.
- `TypeScript/` — Worker source (`sqlite-worker.ts` dispatcher,
  `worker-state.ts`, `sqlite-logger.ts`, `type-conversion.ts`,
  `bulk-ops.ts`, `ef-core-functions.ts`, plus the currently-colocated
  Plane 2 worker modules `worker-manifest.ts` / `crypto-header.ts` /
  `crypto-permissions.ts` / `crypto-delta.ts` / `vfs-prf/*` that will
  move with the engine in Phase 4 of the plane-split pass).
