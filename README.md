# SqliteWasmBlazor

**The first known solution providing true filesystem-backed SQLite database with full EF Core support for Blazor WebAssembly.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/badge/NuGet-Coming%20Soon-orange.svg)]()

<!-- Live demo temporarily disabled for updates -->
<!-- **[🚀 Try the Live Demo](https://b-straub.github.io/SqliteWasmBlazor/)** - Experience persistent SQLite database in your browser! -->

## What Makes This Special?

Unlike other Blazor WASM database solutions that use in-memory storage or IndexedDB emulation, **SqliteWasmBlazor** is the **first implementation** that combines:

✅ **True Filesystem Storage** - Uses OPFS (Origin Private File System) with synchronous access handles
✅ **Full EF Core Support** - Complete ADO.NET provider with migrations, relationships, and LINQ
✅ **Real SQLite Engine** - Official sqlite-wasm (3.50.4) running in Web Worker
✅ **Persistent Data** - Survives page refreshes, browser restarts, and even browser updates
✅ **No Server Required** - Everything runs client-side in the browser

## Why This Matters

Traditional Blazor WASM database solutions have significant limitations:

| Solution | Storage | Persistence | EF Core | Limitations |
|----------|---------|-------------|---------|-------------|
| **InMemory** | RAM | ❌ None | ✅ Full | Lost on refresh |
| **IndexedDB** | IndexedDB | ✅ Yes | ⚠️ Limited | No SQL, complex API |
| **SQL.js** | IndexedDB | ✅ Yes | ❌ None | Manual serialization |
| **besql** | Cache API | ✅ Yes | ⚠️ Partial | Emulated filesystem |
| **SqliteWasmBlazor** | **OPFS** | **✅ Yes** | **✅ Full** | **None!** |

**SqliteWasmBlazor** is the only solution that provides a real, persistent filesystem-backed SQLite database with complete EF Core support, including migrations, complex queries, relationships, and all LINQ operators.

## Architecture

### The Innovation: Worker-Based Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                   Blazor WebAssembly                        │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │              EF Core DbContext                        │  │
│  │   Migrations • LINQ • Relationships • Tracking        │  │
│  └─────────────────────┬─────────────────────────────────┘  │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │        SqliteWasmBlazor ADO.NET Provider              │  │
│  │    Connection • Command • DataReader • Transaction    │  │
│  └─────────────────────┬─────────────────────────────────┘  │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │         .NET SQLite Stub (8KB e_sqlite3.a)            │  │
│  │           Minimal shim - forwards to Worker           │  │
│  └─────────────────────┬─────────────────────────────────┘  │
│                        │                                    │
│                        │ Request (JSON)                     │
│                        │ SQL + Parameters (~1KB)            │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │          Web Worker (sqlite-worker.ts)                │  │
│  ├───────────────────────────────────────────────────────┤  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │       SQLite Engine (sqlite-wasm)               │  │  │
│  │  │  • Executes ALL SQL queries                     │  │  │
│  │  │  • Handles transactions, indexes, joins         │  │  │
│  │  │  • Direct OPFS SAHPool VFS access               │  │  │
│  │  └──────────────────┬──────────────────────────────┘  │  │
│  │                     ▼                                 │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │    OPFS SAHPool VFS (Persistent Storage)        │  │  │
│  │  │  • Real filesystem API (not emulated)           │  │  │
│  │  │  • Synchronous access handles                   │  │  │
│  │  │  • /databases/YourDb.db                         │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
│                        │                                    │
│                        │ Response (MessagePack)             │
│                        │ Results + Metadata (~60% smaller)  │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │                 Back to EF Core                       │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### How It Works

This architecture bridges EF Core with OPFS-backed SQLite:

1. **EF Core needs .NET ADO.NET** - The official `DbConnection` interface for database operations
2. **OPFS needs Web Worker** - Synchronous file access (SAHPool) only available in Web Workers
3. **Workers can't run .NET** - Web Workers cannot execute the main .NET runtime

**Solution:** Minimal native stub + Worker-based SQLite:
- **.NET Stub** (Main thread): Tiny 8KB shim implementing `DbConnection` interface, forwards to Worker
- **SQLite Engine** (Web Worker): Full sqlite-wasm executes queries directly on OPFS SAHPool

**Communication Protocol:**
- **Requests (.NET → Worker)**: JSON serialized (SQL string + parameters) - typically < 1KB
- **Responses (Worker → .NET)**: MessagePack serialized (query results) - optimized for large datasets

All SQL queries execute in the Worker thread against the OPFS-backed database file.

## Features

### 🎯 Full EF Core Support

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

### 🚀 High Performance

- **Efficient Serialization** - JSON for requests (small), MessagePack for responses (optimized for data)
- **Typed Column Information** - Worker sends type metadata to reduce .NET marshalling overhead
- **OPFS SAHPool** - Near-native filesystem performance with synchronous access
- **Direct Execution** - Queries run directly on persistent storage, no copying needed

### 🛡️ Enterprise-Ready

- **Type Safety** - Full .NET type system with proper decimal support
- **EF Core Functions** - All `ef_*` scalar and aggregate functions implemented
- **JSON Collections** - Store `List<T>` with proper value comparers
- **Logging** - Configurable logging levels (Debug/Info/Warning/Error)
- **Error Handling** - Proper async error propagation

<!-- Live demo temporarily disabled for updates -->
<!--
## Try It Now

### Live Demo

**[👉 Launch the demo application](https://b-straub.github.io/SqliteWasmBlazor/)** to see SqliteWasmBlazor in action:

- ✅ Create, edit, and delete todo items with full CRUD operations
- ✅ Full-text search (FTS5) with real-time highlighting
- ✅ Data persists across page refreshes and browser restarts
- ✅ All operations run entirely in your browser (no server required)
- ✅ View database statistics and performance metrics

Open the demo in multiple tabs to see the multi-tab conflict detection in action!
-->

## Installation

### NuGet Package (Coming Soon)

```bash
dotnet add package SqliteWasmBlazor
```

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

var host = builder.Build();

// Initialize SqliteWasm database with automatic migration support
await host.Services.InitializeSqliteWasmDatabaseAsync<TodoDbContext>();

// Configure logging (optional)
SqliteWasmLogger.SetLogLevel(SqliteWasmLogLevel.WARNING);

await host.RunAsync();
```

The `InitializeSqliteWasmDatabaseAsync` extension method automatically:
- Initializes the Web Worker bridge
- Applies pending migrations (with automatic migration history recovery)
- Handles multi-tab conflicts with helpful error messages
- Tracks initialization status via `IDBInitializationService`

### 2. Define Your DbContext

**Data/TodoDbContext.cs:**

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

**Pages/Todos.razor:**

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

## Advanced Features

### Migrations

EF Core migrations work with SqliteWasmBlazor, but require special configuration due to WebAssembly limitations.

**Project Structure Recommendation:**
- Put your DbContext and models in a **separate project** (e.g., `YourApp.Models`)
- Reference this project from your Blazor WebAssembly project
- Configure `Microsoft.EntityFrameworkCore.Design` with minimal assets:

```xml
<!-- In YourApp.Models.csproj -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0">
    <IncludeAssets>runtime; analyzers;</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

This prevents design-time assets from being published with your WebAssembly app, which would cause errors.

**Generate and apply migrations:**

```bash
# Generate migration (run from models project directory)
dotnet ef migrations add InitialCreate --context TodoDbContext

# Apply migrations at runtime (in your Blazor app)
await dbContext.Database.MigrateAsync();
```

The `InitializeSqliteWasmDatabaseAsync` extension method automatically applies pending migrations during app startup.

### Full-Text Search (FTS5)

SqliteWasmBlazor supports SQLite's FTS5 (Full-Text Search 5) virtual tables for powerful text search capabilities:

```csharp
// Define FTS5 virtual table entity
public class FTSTodoItem
{
    public int RowId { get; set; }
    public string? Match { get; set; }
    public double Rank { get; set; }
    public TodoItem? TodoItem { get; set; }
}

// Configure in OnModelCreating
modelBuilder.Entity<FTSTodoItem>(entity =>
{
    entity.HasNoKey();
    entity.ToTable("FTSTodoItem");
    entity.Property(e => e.Match).HasColumnName("FTSTodoItem");
});

// Create FTS5 table via migration (manually edit migration file)
migrationBuilder.Sql(@"
    CREATE VIRTUAL TABLE FTSTodoItem USING fts5(
        Title, Description,
        content='TodoItems',
        content_rowid='Id'
    );
");

// Search with highlighting
var results = dbContext.Database
    .SqlQuery<TodoItemSearchResult>($@"
        SELECT
            t.Id, t.Title, t.Description,
            highlight(FTSTodoItem, 0, '<mark>', '</mark>') AS HighlightedTitle,
            highlight(FTSTodoItem, 1, '<mark>', '</mark>') AS HighlightedDescription,
            rank AS Rank
        FROM FTSTodoItem
        INNER JOIN TodoItems t ON FTSTodoItem.rowid = t.Id
        WHERE FTSTodoItem MATCH {searchQuery}
        ORDER BY rank")
    .AsNoTracking();
```

**FTS5 Features:**
- Full-text search across multiple columns with relevance ranking
- `highlight()` function for marking search matches in full text
- `snippet()` function for contextual excerpts with configurable token limits
- Automatic query sanitization to handle special characters safely
- Support for phrase searches, prefix matching, and boolean operators

See the demo application for a complete FTS5 implementation example.

### JSON Collections

Store complex types as JSON:

```csharp
public class MyEntity
{
    public int Id { get; set; }
    public List<int> Numbers { get; set; }
}

// In OnModelCreating:
entity.Property(e => e.Numbers)
    .HasConversion(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions?)null) ?? new()
    )
    .Metadata.SetValueComparer(
        new ValueComparer<List<int>>(
            (c1, c2) => c1!.SequenceEqual(c2!),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToList()
        )
    );
```

### Logging Configuration

```csharp
// Set worker log level
SqliteWasmLogger.SetLogLevel(SqliteWasmLogLevel.WARNING);

// Configure EF Core logging
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Infrastructure", LogLevel.Error);
```

## Browser Support

| Browser | Version | OPFS Support | Status |
|---------|---------|--------------|--------|
| Chrome  | 108+    | ✅ Full SAH support | ✅ Recommended |
| Edge    | 108+    | ✅ Full SAH support | ✅ Recommended |
| Firefox | 111+    | ✅ Full SAH support | ✅ Supported |
| Safari  | 16.4+   | ✅ Full SAH support | ✅ Supported |

All modern browsers (2023+) support OPFS with Synchronous Access Handles.

## Technical Details

### Package Size (Published/Release Build)

- **SqliteWasmBlazor.wasm**: 88 KB (ADO.NET provider + EF Core integration)
- **sqlite-wasm-worker.js**: 234 KB (minified, includes MessagePack)
- **sqlite-wasm-bridge.js**: 1.7 KB (main thread bridge)
- **sqlite3.wasm**: 836 KB (official SQLite WebAssembly build)
- **Total overhead**: ~1.16 MB (compressed sizes are typically 40-50% smaller)

### Performance Characteristics

- **Initial Load**: ~100-200ms (worker initialization + OPFS setup)
- **Query Execution**: < 1ms for simple queries, 10-50ms for complex joins
- **Persistence**: Automatic after `SaveChanges()`, ~10-30ms overhead
- **Database Size**: Limited only by OPFS quota (typically several GB per origin)

### SQLite Configuration

Automatically configured for OPFS environment (SQLite 3.47+):

```sql
PRAGMA locking_mode = exclusive;  -- Required for WAL mode with OPFS
PRAGMA journal_mode = WAL;        -- Write-Ahead Logging for performance
PRAGMA synchronous = FULL;        -- Maximum data safety
```

**Note**: WAL mode with OPFS requires exclusive locking (single connection). This is automatically handled - no concurrency concerns in single-user browser environment.

### Custom EF Core Functions

All EF Core functions are implemented for full compatibility:

- **Arithmetic**: `ef_add`, `ef_divide`, `ef_multiply`, `ef_mod`, `ef_negate`
- **Comparison**: `ef_compare`
- **Aggregates**: `ef_sum`, `ef_avg`, `ef_min`, `ef_max` (optimized via native SQLite)
- **Pattern Matching**: `regexp` (for `Regex.IsMatch()`)
- **Collation**: `EF_DECIMAL` (for proper decimal sorting)

## FAQ

### How is this different from besql?

besql uses Cache Storage API to emulate a filesystem. SqliteWasmBlazor uses **real OPFS filesystem** with synchronous access, providing true native-like performance and the ability to run the actual .NET SQLite provider.

### Can I use this in production?

Yes! The technology is stable (OPFS is a W3C standard), and all major browsers support it. The library has been tested with complex real-world scenarios.

### What about mobile browsers?

Mobile Chrome (Android 108+) and Safari (iOS 16.4+) both support OPFS with synchronous access handles.

### How do I export/backup my database?

The database files are in OPFS at `/databases/YourDb.db`. You can access them via the sqlite-worker for export functionality (feature coming soon).

### Is this compatible with existing EF Core code?

Yes! All standard EF Core features work: migrations, relationships, LINQ queries, change tracking, etc.

## Roadmap

- [x] Core ADO.NET provider
- [x] OPFS SAHPool integration
- [x] EF Core migrations support
- [x] MessagePack serialization
- [x] Custom EF functions (decimals)
- [x] FTS5 full-text search with highlighting and snippets
- [x] MudBlazor demo app
- [ ] NuGet package release
- [ ] Database export/import API
- [ ] Multi-database support
- [ ] Backup/restore utilities
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

**Built with ❤️ for the Blazor community**

If you find this useful, please ⭐ star the repository!
