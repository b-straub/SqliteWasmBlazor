# SqliteWasmBlazor

**The first known solution providing true filesystem-backed SQLite database with full EF Core support for Blazor WebAssembly.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/vpre/SqliteWasmBlazor)](https://www.nuget.org/packages/SqliteWasmBlazor)
[![GitHub Repo stars](https://img.shields.io/github/stars/b-straub/SqliteWasmBlazor)](https://github.com/b-straub/SqliteWasmBlazor/stargazers)

**[Try the Live Demo](https://b-straub.github.io/SqliteWasmBlazor/)** - Experience persistent SQLite database in your browser! Can be installed as a Progressive Web App (PWA) for offline use.

## Related Projects

SqliteWasmBlazor is part of a family of libraries for building offline-first Blazor applications:

| Project | Description |
|---------|-------------|
| **[RxBlazorV2](https://github.com/b-straub/RxBlazorV2)** | Reactive programming framework for Blazor built on [R3](https://github.com/Cysharp/R3). Uses Roslyn source generators for observable models with reactive property bindings, command patterns, and automatic component generation. |
| **[BlazorPRF](https://github.com/b-straub/BlazorPRF)** | PRF-based deterministic encryption for Blazor WebAssembly using the WebAuthn PRF extension. Enables client-side encryption with biometric authentication — keys derived from passkeys for passwordless encrypt/decrypt workflows. |

Together these enable a complete offline-first stack: persistent local storage (SqliteWasmBlazor) + reactive state management (RxBlazorV2) + client-side encryption with biometric auth (BlazorPRF).

**Coming soon:** [BlazPulse](https://github.com/b-straub/BlazPulse) — a universal offline-first delta sync framework tying all of these together. Bidirectional sync between distributed SQLite databases with hash-protected incremental deltas and Ed25519 cryptographic authentication.

## About This Project

This is a non-commercial hobby project maintained in my spare time - no fixed update cycle or roadmap. However, "hobby" refers to time and commitment, not craftsmanship: the project is developed with professional standards including proper test coverage and attention to code quality.

Open source thrives on community involvement. The project grows through bug reports, feature requests, pull requests, and real-world feedback. If you're considering this for production use, I'd encourage you to contribute back - that's how open source stays alive, not through promises from a single maintainer, but through shared ownership.

### Stability & Status

The public API surface is intentionally kept minimal to reduce the risk of breaking changes. While the API has been stable in practice, this project is pre-1.0: broader real-world feedback is needed before committing to long-term API guarantees. Contributions and usage reports help move toward that goal.

## What's New

- **V2 Worker-Side Bulk Import/Export** - Worker-side prepared statement loops for 10-50x faster import. Self-describing V2 MessagePack format with column metadata. Memory-safe streaming for large datasets [(details)](CHANGELOG.md#v2-worker-side-bulk-importexport)
- **Multi-Part Export** - Large databases automatically split into manageable parts with a meta file. Adaptive part sizing based on configurable MB limit
- **Seed Server** - PHP REST API + Blazor UI for uploading/downloading seed files to/from a server. One-click database provisioning for new clients [(details)](#seed-server)
- **Raw Database Import/Export** - Export and import complete .db files directly from/to OPFS with schema validation and automatic backup/restore on failure [(details)](CHANGELOG.md#raw-database-importexport)
- **Multi-Database Support** - Run multiple independent SQLite databases simultaneously in the same Web Worker [(details)](docs/multi-database.md)
- **Incremental Database Export/Import** - File-based delta sync with checkpoint management and conflict resolution [(details)](CHANGELOG.md#incremental-database-exportimport-delta-sync)
- **Real-World Sample** - Check out the [Datasync TodoApp](https://github.com/b-straub/Datasync/tree/main/samples/todoapp-blazor-wasm-offline) for offline-first data synchronization with SqliteWasmBlazor

## Breaking Changes

- **v0.9.0-pre** — CSP hardening: removed the inline `data:text/javascript` import that
  auto-detected `<base href>`. The worker URL is now built from `SqliteWasmOptions.BaseHref`
  (default `"/"`).

  Apps deployed on a **sub-path** must now opt in explicitly:
  ```csharp
  builder.Services.AddSqliteWasm(o => o.HostEnvironment = builder.HostEnvironment);
  ```
  This derives `BaseHref` from the runtime `<base href>` exactly as the old auto-probe did,
  but without the CSP-blocked `data:` import. Root-path (`/`) deployments need no change.

  **Browser-extension** builds (which flatten the `_content/` underscore prefix) override
  the asset root:
  ```csharp
  builder.Services.AddSqliteWasm(o => o.AssetRoot = "content/SqliteWasmBlazor/");
  ```

  Companion packages gained the same options pattern:
  - `Components`: `FileOperationsInterop.InitializeAsync(o => o.AssetRoot = "...")`
  - `FloatingWindow`: `services.AddFloatingWindow(o => o.AssetRoot = "...")`

  Calling a database operation before `InitializeSqliteWasmAsync` /
  `InitializeSqliteWasmDatabaseAsync<T>` now throws `InvalidOperationException` with a clear
  message, instead of silently lazy-initialising with default settings (which 404'd on
  sub-path / extension builds).

- **v0.7.2-pre** - `SqliteWasmWorkerBridge` is now internal. Use `ISqliteWasmDatabaseService` via DI instead:
  ```csharp
  // Program.cs - add service registration
  builder.Services.AddSqliteWasm();

  // Components - inject the interface
  @inject ISqliteWasmDatabaseService DatabaseService

  // Replace SqliteWasmWorkerBridge.Instance.DeleteDatabaseAsync(...)
  // with:   DatabaseService.DeleteDatabaseAsync(...)
  ```

## What Makes This Special?

Unlike other Blazor WASM database solutions that use in-memory storage or IndexedDB emulation, **SqliteWasmBlazor** is the **first implementation** that combines:

- **True Filesystem Storage** - Uses OPFS (Origin Private File System) with synchronous access handles
- **Full EF Core Support** - Complete ADO.NET provider with migrations, relationships, and LINQ
- **Real SQLite Engine** - Official sqlite-wasm (3.50.4) running in Web Worker
- **Persistent Data** - Survives page refreshes, browser restarts, and even browser updates
- **No Server Required** - Everything runs client-side in the browser

| Solution | Storage | Persistence | EF Core | Limitations |
|----------|---------|-------------|---------|-------------|
| **InMemory** | RAM | None | Full | Lost on refresh |
| **IndexedDB** | IndexedDB | Yes | Limited | No SQL, complex API |
| **SQL.js** | IndexedDB | Yes | None | Manual serialization |
| **besql** | Cache API | Yes | Partial | Emulated filesystem |
| **SqliteWasmBlazor** | **OPFS** | **Yes** | **Full** | **None!** |

## Public API

SqliteWasmBlazor exposes a **stable public API** for database management operations via dependency injection:

### ISqliteWasmDatabaseService

The primary interface for database operations outside of EF Core:

```csharp
public interface ISqliteWasmDatabaseService
{
    // Database management
    Task<bool> ExistsDatabaseAsync(string databaseName, CancellationToken ct = default);
    Task DeleteDatabaseAsync(string databaseName, CancellationToken ct = default);
    Task RenameDatabaseAsync(string oldName, string newName, CancellationToken ct = default);
    Task CloseDatabaseAsync(string databaseName, CancellationToken ct = default);

    // Raw .db file import/export
    Task ImportDatabaseAsync(string databaseName, byte[] data, CancellationToken ct = default);
    Task<byte[]> ExportDatabaseAsync(string databaseName, CancellationToken ct = default);

    // V2 bulk import/export (worker-side prepared statement loops)
    Task<int> BulkImportAsync(string databaseName, byte[] payload,
        ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.None,
        CancellationToken ct = default);
    Task<byte[]> BulkExportAsync(string databaseName, BulkExportMetadata metadata,
        CancellationToken ct = default);
}
```

**Usage in components:**

```csharp
@inject ISqliteWasmDatabaseService DatabaseService

@code {
    private async Task ResetDatabaseAsync()
    {
        // Delete and recreate database
        await DatabaseService.DeleteDatabaseAsync("MyApp.db");

        await using var context = await DbContextFactory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    private async Task ExportAsync()
    {
        // Export raw .db file (closes DB for consistent snapshot, auto-reopens on next query)
        byte[] data = await DatabaseService.ExportDatabaseAsync("MyApp.db");
    }

    private async Task ImportAsync(byte[] data)
    {
        // Import raw .db file (validates SQLite header)
        await DatabaseService.ImportDatabaseAsync("MyApp.db", data);
    }
}
```

### Other Public Types

| Type | Purpose |
|------|---------|
| `SqliteWasmConnection` | ADO.NET `DbConnection` for direct SQL access |
| `SqliteWasmCommand` | ADO.NET `DbCommand` for query execution |
| `SqliteWasmDataReader` | ADO.NET `DbDataReader` for result iteration |
| `SqliteWasmParameter` | ADO.NET `DbParameter` for query parameters |
| `SqliteWasmTransaction` | ADO.NET `DbTransaction` for transaction support |
| `IDBInitializationService` | Tracks database initialization state and errors |

All internal implementation details (worker bridge, serialization, etc.) are encapsulated and not part of the public API.

## Installation

### NuGet Package

```bash
dotnet add package SqliteWasmBlazor --prerelease
```

Or install a specific version:

```bash
dotnet add package SqliteWasmBlazor --version 0.6.5-pre
```

Visit [NuGet.org](https://www.nuget.org/packages/SqliteWasmBlazor) for the latest version.

### From Source

```bash
git clone https://github.com/bernisoft/SqliteWasmBlazor.git
cd SqliteWasmBlazor
dotnet build
```

## Quick Start

### 1. Configure Your Project

**Program.cs:**

```csharp
using SqliteWasmBlazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add your DbContext with SqliteWasm provider
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db");
    options.UseSqliteWasm(connection);
});

// Register initialization service
builder.Services.AddSingleton<IDBInitializationService, DBInitializationService>();

// Register SqliteWasm database management service (for ISqliteWasmDatabaseService)
builder.Services.AddSqliteWasm();

var host = builder.Build();

// Initialize SqliteWasm database with automatic migration support
await host.Services.InitializeSqliteWasmDatabaseAsync<TodoDbContext>();

await host.RunAsync();
```

The `InitializeSqliteWasmDatabaseAsync` extension method automatically:
- Initializes the Web Worker bridge
- Applies pending migrations (with automatic migration history recovery)
- Handles multi-tab conflicts with helpful error messages
- Tracks initialization status via `IDBInitializationService`

### 2. Define Your DbContext

```csharp
using Microsoft.EntityFrameworkCore;

public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions<TodoDbContext> options) : base(options) { }

    public DbSet<TodoItem> TodoItems { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TodoItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
        });
    }
}

public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

### 3. Use in Your Components

```razor
@inject IDbContextFactory<TodoDbContext> DbFactory

<h3>Todo List</h3>

@foreach (var todo in todos)
{
    <div>
        <input type="checkbox" @bind="todo.IsCompleted" @bind:after="() => SaveTodo(todo)" />
        <span>@todo.Title</span>
    </div>
}

@code {
    private List<TodoItem> todos = new();

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        todos = await db.TodoItems.OrderBy(t => t.CreatedAt).ToListAsync();
    }

    private async Task SaveTodo(TodoItem todo)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        db.TodoItems.Update(todo);
        await db.SaveChangesAsync(); // Automatically persists to OPFS!
    }
}
```

## Features

### Full EF Core Support

```csharp
// Migrations
await dbContext.Database.MigrateAsync();

// Complex queries with LINQ
var results = await dbContext.Orders
    .Include(o => o.Customer)
    .Where(o => o.Total > 100)
    .OrderByDescending(o => o.Date)
    .ToListAsync();

// Relationships
public class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; }
    public List<OrderItem> Items { get; set; }
}

// Decimal arithmetic (via ef_ scalar functions)
var expensive = await dbContext.Products
    .Where(p => p.Price * 1.2m > 100m)
    .ToListAsync();
```

### High Performance

- **Efficient Serialization** - JSON for requests (small), MessagePack for responses (optimized for data)
- **Typed Column Information** - Worker sends type metadata to reduce .NET marshalling overhead
- **OPFS SAHPool** - Near-native filesystem performance with synchronous access
- **Direct Execution** - Queries run directly on persistent storage, no copying needed

### Enterprise-Ready

- **Type Safety** - Full .NET type system with proper decimal support
- **EF Core Functions** - All `ef_*` scalar and aggregate functions implemented
- **JSON Collections** - Store `List<T>` with proper value comparers
- **Logging** - Configurable logging levels (Debug/Info/Warning/Error)
- **Error Handling** - Proper async error propagation

## Documentation

| Topic | Description |
|-------|-------------|
| [Architecture](docs/architecture.md) | Worker-based architecture, how it works, technical details |
| [ADO.NET Usage](docs/ado-net.md) | Using SqliteWasmBlazor without EF Core, transactions |
| [Advanced Features](docs/advanced-features.md) | Migrations, FTS5 search, JSON collections, logging |
| [Multi-Database](docs/multi-database.md) | Running multiple databases, cross-database references |
| [Bulk Import/Export](docs/bulk-import-export.md) | V2 format, multi-part export, delta sync, type conversions |
| [Seed Server](docs/seed-server.md) | PHP API setup, cloud seeding, Herd/Apache configuration |
| [Recommended Patterns](docs/patterns.md) | Multi-view pattern, data initialization best practices |
| [FAQ](docs/faq.md) | Common questions and browser support |
| [Changelog](CHANGELOG.md) | Release notes and version history |

## Seed Server

The Seed Server enables cloud-based database provisioning — upload seed data from one client and download it on another. Useful for initial client-side database setup without building the data locally.

### Setup

The `SeedApi/` folder contains a PHP REST API:

```bash
# Local development with Herd/Valet
cd SeedApi
herd link seed-api
herd secure seed-api

# Production (Apache) — just copy these files:
# seed-api.php, index.php, .htaccess
```

Configure the client in `wwwroot/appsettings.json`:
```json
{
  "seedApiUrl": "seed-api.test",
  "exportPartSizeMb": 250,
  "cloudPartSizeMb": 40
}
```

The `seedApiUrl` is the server domain (without protocol). It can be temporarily overridden on the Seed Server page — useful for testing with the deployed GitHub Pages demo.

### API Endpoints

| Method | URL | Description |
|--------|-----|-------------|
| GET | `/api/seeds` | List all seeds |
| GET | `/api/seeds/{name}` | Seed metadata |
| GET | `/api/seeds/{name}/{file}` | Download file |
| POST | `/api/seeds/{name}` | Upload file (multipart form) |
| DELETE | `/api/seeds/{name}` | Delete seed + all parts |

### Configuration

- `exportPartSizeMb` — max part size in MB for local file export (default: 250)
- `cloudPartSizeMb` — max part size in MB for cloud upload (auto-synced from `.htaccess` at build time, default: 40)
- PHP upload limits in `SeedApi/.htaccess` control the server-side maximum

See [Seed Server docs](docs/seed-server.md) for detailed setup instructions.

## Browser Support

| Browser | Version | OPFS Support |
|---------|---------|--------------|
| Chrome  | 108+    | Full SAH support |
| Edge    | 108+    | Full SAH support |
| Firefox | 111+    | Full SAH support |
| Safari  | 16.4+   | Full SAH support |

All modern browsers (2023+) support OPFS with Synchronous Access Handles, including mobile browsers (iOS/iPadOS Safari, Android Chrome).

## Roadmap

- [x] Core ADO.NET provider
- [x] OPFS SAHPool integration
- [x] EF Core migrations support
- [x] MessagePack serialization
- [x] Custom EF functions (decimals)
- [x] FTS5 full-text search with highlighting and snippets
- [x] MudBlazor demo app
- [x] NuGet package pre-release
- [x] Database export/import API (MessagePack serialization + raw .db files)
- [x] Backup/restore utilities (delta sync with checkpoints)
- [x] Raw database import/export with schema validation
- [x] Multi-database support
- [x] V2 worker-side bulk import/export with prepared statement loops
- [x] Multi-part export for large databases
- [x] Seed Server API for cloud-based database provisioning
- [ ] Stable NuGet package release
- [ ] Server-side delta generation from SQLite databases

## Contributing

Issues, bug reports, and feature discussions are always welcome.

For code contributions we use an **issue-first policy**: please open an issue and agree on the approach before submitting a pull request. Trivial fixes (typos, obvious one-line bugs) can be sent directly. See [CONTRIBUTING.md](CONTRIBUTING.md) for details and rationale.

## Credits

**Author**: bernisoft
**License**: MIT

Built with:
- [SQLite](https://sqlite.org) - The world's most deployed database
- [sqlite-wasm](https://sqlite.org/wasm) - Official SQLite WebAssembly build
- [Entity Framework Core](https://github.com/dotnet/efcore) - Modern data access
- [MessagePack](https://msgpack.org/) - Efficient binary serialization
- [MudBlazor](https://mudblazor.com/) - Material Design components

## License

MIT License - Copyright (c) 2025 bernisoft

See [LICENSE](LICENSE) file for details.

---

**Built with love for the Blazor community**

If you find this useful, please star the repository!
