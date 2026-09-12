# SqliteWasmBlazor

**True filesystem-backed SQLite with full EF Core support for Blazor WebAssembly.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/vpre/SqliteWasmBlazor)](https://www.nuget.org/packages/SqliteWasmBlazor)
[![GitHub Repo stars](https://img.shields.io/github/stars/b-straub/SqliteWasmBlazor)](https://github.com/b-straub/SqliteWasmBlazor/stargazers)

A real SQLite engine (official [sqlite-wasm](https://sqlite.org/wasm)) runs in a Web Worker on
OPFS with synchronous access handles; your app talks to it through a complete ADO.NET provider
and EF Core. Data survives refreshes, restarts and browser updates. No server, no emulated
filesystem, no manual serialization.

**[Try the live demo](https://b-straub.github.io/SqliteWasmBlazor/)** — installable as a PWA for offline use.

| Solution | Storage | Persistence | EF Core |
|----------|---------|-------------|---------|
| InMemory | RAM | none | full |
| IndexedDB | IndexedDB | yes | limited — no SQL |
| SQL.js | IndexedDB | yes | none — manual serialization |
| besql | Cache API | yes | partial — emulated filesystem |
| **SqliteWasmBlazor** | **OPFS** | **yes** | **full** |

## Install

```bash
dotnet add package SqliteWasmBlazor --prerelease
```

Optional at-rest encryption and its drop-in UI are separate packages:

```bash
dotnet add package SqliteWasmBlazor.Crypto --prerelease     # encrypted VFS
dotnet add package SqliteWasmBlazor.Crypto.UI --prerelease  # auth / encryption panels
```

Upgrading from an earlier pre-release? Breaking changes are listed per version in the
[Changelog](CHANGELOG.md).

## Quick Start

**Program.cs**

```csharp
using SqliteWasmBlazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db");
    options.UseSqliteWasm(connection);
});

builder.Services.AddSqliteWasm();

builder.Services.AddSqliteWasmDbContext<TodoDbContext>();

var host = builder.Build();
await host.RunAsync();
```

Then place the initializer once, in your layout:

```razor
<SqliteWasmDatabaseInitializer/>
```

`Program.cs` only registers. `<SqliteWasmDatabaseInitializer/>` renders nothing;
after the first render it starts the worker and applies pending migrations for
every context declared with `AddSqliteWasmDbContext<T>()`. A migration that
cannot be applied — the schema on disk disagrees with the migrations in the
assembly — reports `SCHEMA_INCOMPATIBLE` with SQLite's reason; the remedy is a
reset, which `<DatabaseInformationAlert/>` offers. Progress and failures are reported through
`IDbInitializationStatus`.

Initializing after the app renders is what lets a long migration be reported
instead of freezing a blank page — and it is the only way an encrypted pool can
be opened at all, since its key comes from a WebAuthn ceremony that needs a UI.

Apps deployed on a sub-path must set the base href explicitly:

```csharp
builder.Services.AddSqliteWasm(o => o.BaseHref =
    new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath);
```

**Use it**

```razor
@inject IDbContextFactory<TodoDbContext> DbFactory

@code {
    private async Task SaveTodo(TodoItem todo)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        db.TodoItems.Update(todo);
        await db.SaveChangesAsync(); // persisted to OPFS
    }
}
```

Your `DbContext` is an ordinary EF Core context — migrations, LINQ, `Include`, relationships and
decimal arithmetic all work as usual.

## Public API

`ISqliteWasmDatabaseService` (via DI) covers database management outside of EF Core. Every file
path streams; none of them holds a database in managed memory.

```csharp
public interface ISqliteWasmDatabaseService
{
    bool CanCancelQueries { get; }   // a service worker carries cancels to the running statement

    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default);
    Task<bool> ExistsDatabaseAsync(string databaseName, CancellationToken ct = default);
    Task DeleteDatabaseAsync(string databaseName, CancellationToken ct = default);
    Task RenameDatabaseAsync(string oldName, string newName, CancellationToken ct = default);
    Task CloseDatabaseAsync(string databaseName, CancellationToken ct = default);

    Task ExportDatabaseToStreamAsync(string databaseName, Stream destination,
        CancellationToken ct = default);
    Task ExportDatabaseToDownloadAsync(string databaseName, string filename,
        CancellationToken ct = default);
    Task ExportDatabasesToDownloadAsync(IReadOnlyList<string> databaseNames,
        string filename, CancellationToken ct = default);
    Task ImportDatabaseFromStreamAsync(string databaseName, Stream stream, long size,
        Func<string, CancellationToken, ValueTask>? validateImported = null,
        CancellationToken ct = default);
    Task ImportDatabasesFromStreamAsync(Stream envelopeStream, long envelopeSize,
        Func<string, CancellationToken, ValueTask>? validateImported = null,
        CancellationToken ct = default);

    Task<int> ImportRowsAsync(string databaseName, byte[] data,
        CancellationToken ct = default);
}
```

Imports park what they replace, run the incoming file past an optional `validateImported`
check, and re-run the host's migrations before the import counts; a refusal restores the
previous files byte-identically.

| Type | Purpose |
|------|---------|
| `SqliteWasmConnection` / `Command` / `DataReader` / `Parameter` / `Transaction` | ADO.NET provider for direct SQL |
| `IDbInitializationStatus` | Initialization state and errors |
| `PoolImportResult` | Outcome of a raw `.db` import |
| `SchemaMismatchException` | Thrown by `ValidateImportedSchemaAsync`; carries `MissingTables` |
| `PoolOperationRejectedException` | Typed precondition refusal, carries `Reason` |

Everything else — worker bridge, serialization, VFS — is internal.

## Optional: at-rest encryption

`SqliteWasmBlazor.Crypto` adds an encryption layer to the same VFS. Without a registered key it
falls through to byte-for-byte vendor SAHPool behavior.

- Every 4 096-byte page is sealed with **ChaCha20-Poly1305**, with the AEAD bound to
  `(versionTag, dbPath, slotIndex)` so relocated or cross-database pages fail authentication.
- The key is derived from a **passkey via the WebAuthn PRF extension** — no password, no key file.
- Unlock is verified (slot-0 AEAD probe + manifest MAC), not silent.
- Encrypted whole-pool export (`.eds`) wraps the key for a recipient X25519 pubkey.
- `SqliteWasmBlazor.Crypto.UI` ships drop-in RxBlazorV2 panels (en + de), requires
  `RxBlazorV2.MudBlazor` 1.2.6+.
- Machine-checked: 3 Tamarin theories, 74 lemmas, all verified.

Details: [Encrypted VFS](docs/crypto-vfs.md) · [Security](docs/security/README.md) · [Formal models](docs/formal/README.md)

## Documentation

| Topic | Description |
|-------|-------------|
| [Architecture](docs/architecture.md) | Worker-based architecture and technical details |
| [ADO.NET Usage](docs/ado-net.md) | Using the provider without EF Core, transactions |
| [Advanced Features](docs/advanced-features.md) | Migrations, FTS5 search, JSON collections, logging |
| [Multi-Database](docs/multi-database.md) | Multiple databases, cross-database references |
| [Bulk Import/Export](docs/bulk-import-export.md) | V2 format, multi-part export, delta sync |
| [Encrypted VFS](docs/crypto-vfs.md) | At-rest encryption and threat model |
| [Recommended Patterns](docs/patterns.md) | Multi-view pattern, data initialization |
| [FAQ](docs/faq.md) | Common questions and browser support |
| [Changelog](CHANGELOG.md) | Release notes, breaking changes, version history |

## Browser Support

Chrome/Edge 108+, Firefox 111+, Safari 16.4+ — every browser from 2023 on, including
iOS/iPadOS Safari and Android Chrome. All support OPFS with synchronous access handles.

**Large databases move on mobile.** Exports stage through an OPFS file the browser saves from
disk; imports stream into the worker one chunk at a time. Neither heap ever holds the whole
file, so a multi-hundred-megabyte database transfers on a phone. iOS/iPadOS has the tightest
memory budget of the four engines — building for it is what makes the paths safe everywhere.

Installed to the Home Screen there is no downloads folder, so WebKit presents an export on a
full-screen OS sheet ("Open in …") instead of saving it. The file streams at any size and the
sheet's share menu saves to Files — worth telling your users, since it looks like a failure.

## Related Projects

| Project | Description |
|---------|-------------|
| **[RxBlazorV2](https://github.com/b-straub/RxBlazorV2)** | Reactive framework for Blazor on [R3](https://github.com/Cysharp/R3): source-generated observable models, commands and components. |
| **[BlazorPRF](https://github.com/b-straub/BlazorPRF)** | WebAuthn-PRF encryption primitives — absorbed into this repo as `SqliteWasmBlazor.Crypto`. |

Together: persistent local storage with optional at-rest encryption, plus reactive state
management. **Coming next:** CryptoSync — end-to-end encrypted multi-device delta sync with
per-row permissions and a relay that never sees plaintext.

## Project Status

Pre-1.0. The public API is deliberately small and has been stable in practice, but broader
real-world feedback is needed before committing to long-term guarantees.

Shipped: ADO.NET provider · OPFS SAHPool · EF Core migrations · FTS5 · multi-database ·
V2 worker-side bulk import/export · memory-flat streamed export/import · at-rest encryption
with passkey-derived keys · drop-in auth/encryption UI · Tamarin-verified crypto lifecycle.

Next: stable NuGet release · CryptoSync (E2E encrypted delta sync) · server-side delta generation.

This is a non-commercial hobby project maintained in spare time — no fixed release cycle.
"Hobby" refers to time, not craftsmanship: it is developed with test coverage and attention to
code quality. It grows through bug reports, feature requests and pull requests.

## Contributing

Issues, bug reports and feature discussions are always welcome. For code we use an
**issue-first policy**: open an issue and agree on the approach before submitting a PR.
Trivial fixes can be sent directly. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits

**Author**: bernisoft · **License**: MIT (Copyright © 2025 bernisoft), see [LICENSE](LICENSE)

Built with [SQLite](https://sqlite.org) / [sqlite-wasm](https://sqlite.org/wasm),
[EF Core](https://github.com/dotnet/efcore), [MessagePack](https://msgpack.org/) and
[MudBlazor](https://mudblazor.com/).

If you find this useful, please star the repository.
