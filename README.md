# SqliteWasmBlazor

**The first known solution providing true filesystem-backed SQLite database with full EF Core support for Blazor WebAssembly.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/vpre/SqliteWasmBlazor)](https://www.nuget.org/packages/SqliteWasmBlazor)
[![GitHub Repo stars](https://img.shields.io/github/stars/b-straub/SqliteWasmBlazor)](https://github.com/b-straub/SqliteWasmBlazor/stargazers)

**[Try the Live Demo](https://b-straub.github.io/SqliteWasmBlazor/)** - Experience persistent SQLite database in your browser! Can be installed as a Progressive Web App (PWA) for offline use.

## What's New

- **Multi-View Demo** - Floating draggable/resizable dialog windows using lightweight JS interop on top of standard MudBlazor dialogs [(details)](docs/patterns.md#multi-view-instead-of-multi-tab)
- **Incremental Database Export/Import** - File-based delta sync with checkpoint management and conflict resolution for offline-first PWAs [(details)](CHANGELOG.md#incremental-database-exportimport-delta-sync)
- **Database Import/Export** - Schema-validated MessagePack serialization for backups and data migration [(details)](CHANGELOG.md#database-importexport)
- **Real-World Sample** - Check out the [Datasync TodoApp](https://github.com/b-straub/Datasync/tree/main/samples/todoapp-blazor-wasm-offline) for offline-first data synchronization with SqliteWasmBlazor

## Breaking Changes

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
    /// <summary>Check if a database exists in OPFS.</summary>
    Task<bool> ExistsDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>Delete a database from OPFS.</summary>
    Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>Rename a database in OPFS (atomic operation).</summary>
    Task RenameDatabaseAsync(string oldName, string newName, CancellationToken cancellationToken = default);

    /// <summary>Close a database connection in the worker.</summary>
    Task CloseDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);
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
| [Recommended Patterns](docs/patterns.md) | Multi-view pattern, data initialization best practices |
| [FAQ](docs/faq.md) | Common questions and browser support |
| [Changelog](CHANGELOG.md) | Release notes and version history |

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
- [x] Database export/import API
- [x] Backup/restore utilities (delta sync with checkpoints)
- [ ] Stable NuGet package release
- [ ] Multi-database support
- [ ] Performance profiling tools

## Contributing

Contributions welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Submit a pull request

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
