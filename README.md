# SqliteWasmBlazor

**The first known solution providing true filesystem-backed SQLite database with full EF Core support for Blazor WebAssembly.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/vpre/SqliteWasmBlazor)](https://www.nuget.org/packages/SqliteWasmBlazor)
[![GitHub Repo stars](https://img.shields.io/github/stars/b-straub/SqliteWasmBlazor)](https://github.com/b-straub/SqliteWasmBlazor/stargazers)

**[🚀 Try the Live Demo](https://b-straub.github.io/SqliteWasmBlazor/)** - Experience persistent SQLite database in your browser! Can be installed as a Progressive Web App (PWA) for offline use.

## ✨ What's New

- **Multi-View Demo** - Floating draggable/resizable dialog windows using lightweight JS interop on top of standard MudBlazor dialogs — no external dependencies [(details)](#recommended-pattern-multi-view-instead-of-multi-tab)
- **Incremental Database Export/Import** - File-based delta sync with checkpoint management and conflict resolution for offline-first PWAs [(details)](#incremental-database-exportimport-delta-sync)
- **Database Import/Export** - Schema-validated MessagePack serialization for backups and data migration [(details)](#database-importexport)
- **Real-World Sample** - Check out the [Datasync TodoApp](https://github.com/b-straub/Datasync/tree/main/samples/todoapp-blazor-wasm-offline) for offline-first data synchronization with SqliteWasmBlazor

## ⚠️ Breaking Changes

- **v0.6.7-pre** (2025-11-14) - Log level configuration now uses standard `Microsoft.Extensions.Logging.LogLevel` [(details)](#version-067-pre-2025-11-14)

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

## Try It Now

### Live Demo

**[👉 Launch the demo application](https://b-straub.github.io/SqliteWasmBlazor/)** to see SqliteWasmBlazor in action:

- ✅ Create, edit, and delete todo items with full CRUD operations
- ✅ Full-text search (FTS5) with real-time highlighting
- ✅ Data persists across page refreshes and browser restarts
- ✅ All operations run entirely in your browser (no server required)
- ✅ View database statistics and performance metrics
- ✅ **Install as PWA** for offline use and app-like experience

Open the demo in multiple tabs to see the multi-tab conflict detection in action!

## Installation

### NuGet Package

**Pre-release version available now:**

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

var host = builder.Build();

// Initialize SqliteWasm database with automatic migration support
await host.Services.InitializeSqliteWasmDatabaseAsync<TodoDbContext>();

// Configure logging (optional)
SqliteWasmLogger.SetLogLevel(LogLevel.Warning);

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

## Usage Without Entity Framework Core

SqliteWasmBlazor provides a **complete ADO.NET provider** that can be used standalone, without Entity Framework Core. This is perfect for scenarios where you:

- Want lightweight database access without the EF Core overhead
- Have existing ADO.NET code you want to port to Blazor WASM
- Prefer writing raw SQL queries for full control
- Need to work with large databases (1GB+) efficiently

### Setup for Non-EF Core Usage

**Program.cs:**

```csharp
using SqliteWasmBlazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// No DbContext registration needed for standalone ADO.NET usage!

var host = builder.Build();

// Initialize the SqliteWasm worker bridge
await host.Services.InitializeSqliteWasmAsync();

// Configure logging (optional)
SqliteWasmLogger.SetLogLevel(LogLevel.Warning);

await host.RunAsync();
```

### Using the ADO.NET Provider

SqliteWasmBlazor provides standard ADO.NET classes that work exactly like `Microsoft.Data.Sqlite`:

```csharp
@inject SqliteWasmWorkerBridge WorkerBridge
@implements IAsyncDisposable

<h3>Users</h3>

@foreach (var user in users)
{
    <div>@user.Name (@user.Email)</div>
}

@code {
    private SqliteWasmConnection? connection;
    private List<User> users = new();

    protected override async Task OnInitializedAsync()
    {
        // Create connection
        connection = new SqliteWasmConnection("Data Source=MyApp.db");
        await connection.OpenAsync();

        // Create table if not exists
        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            )";
        await createCmd.ExecuteNonQueryAsync();

        // Query data
        using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = "SELECT Id, Name, Email FROM Users ORDER BY Name";

        using var reader = await queryCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(new User
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Email = reader.GetString(2)
            });
        }
    }

    private async Task AddUser(string name, string email)
    {
        using var cmd = connection!.CreateCommand();
        cmd.CommandText = "INSERT INTO Users (Name, Email, CreatedAt) VALUES ($name, $email, $date)";

        // Add parameters
        cmd.Parameters.Add(new SqliteWasmParameter { ParameterName = "$name", Value = name });
        cmd.Parameters.Add(new SqliteWasmParameter { ParameterName = "$email", Value = email });
        cmd.Parameters.Add(new SqliteWasmParameter { ParameterName = "$date", Value = DateTime.UtcNow.ToString("O") });

        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (connection != null)
        {
            await connection.CloseAsync();
            await connection.DisposeAsync();
        }
    }

    record User
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Email { get; init; } = "";
    }
}
```

### Using Transactions

```csharp
await using var connection = new SqliteWasmConnection("Data Source=MyApp.db");
await connection.OpenAsync();

// Start transaction
await using var transaction = await connection.BeginTransactionAsync();

try
{
    // Execute multiple commands
    await using var cmd1 = connection.CreateCommand();
    cmd1.Transaction = transaction;
    cmd1.CommandText = "INSERT INTO Users (Name, Email) VALUES ('Alice', 'alice@example.com')";
    await cmd1.ExecuteNonQueryAsync();

    await using var cmd2 = connection.CreateCommand();
    cmd2.Transaction = transaction;
    cmd2.CommandText = "UPDATE Users SET Email = 'newemail@example.com' WHERE Name = 'Bob'";
    await cmd2.ExecuteNonQueryAsync();

    // Commit transaction
    await transaction.CommitAsync();
}
catch
{
    // Rollback on error
    await transaction.RollbackAsync();
    throw;
}
```

### Lower-Level Worker Bridge API

For even more control, use the worker bridge directly:

```csharp
@inject SqliteWasmWorkerBridge WorkerBridge

@code {
    protected override async Task OnInitializedAsync()
    {
        // Open database
        await WorkerBridge.OpenDatabaseAsync("MyApp.db");

        // Execute SQL directly
        var result = await WorkerBridge.ExecuteSqlAsync(
            database: "MyApp.db",
            sql: "SELECT Id, Name, Email FROM Users WHERE Id = $0",
            parameters: new Dictionary<string, object?> { ["$0"] = 42 },
            cancellationToken: CancellationToken.None
        );

        // Process results
        foreach (var row in result.Rows)
        {
            var id = (long)row[0];
            var name = (string)row[1];
            var email = (string)row[2];
            // Use the data...
        }

        // Close database when done
        await WorkerBridge.CloseDatabaseAsync("MyApp.db");
    }
}
```

### Key Differences from Microsoft.Data.Sqlite

1. **Use `SqliteWasmConnection` instead of `SqliteConnection`**
   - Same interface, different implementation
   - All operations are async (required for worker communication)

2. **Initialization required**
   - Call `SqliteWasmWorkerBridge.Instance.InitializeAsync()` once at startup
   - This initializes the Web Worker and OPFS

3. **Persistence is automatic**
   - All changes are immediately written to OPFS
   - No manual save/load operations needed (unlike in-memory databases)

4. **Database files stored in OPFS**
   - Persistent across browser restarts
   - Isolated per-origin storage
   - Typically several GB quota available

### Available ADO.NET Classes

All standard ADO.NET types are implemented:

- **`SqliteWasmConnection`** - Database connection (`DbConnection`)
- **`SqliteWasmCommand`** - SQL command execution (`DbCommand`)
- **`SqliteWasmDataReader`** - Forward-only result reading (`DbDataReader`)
- **`SqliteWasmParameter`** - Query parameters (`DbParameter`)
- **`SqliteWasmTransaction`** - Transaction support (`DbTransaction`)

### When to Use ADO.NET vs EF Core

**Use ADO.NET when you:**
- Need lightweight access without EF Core overhead
- Have simple CRUD operations
- Want full control over SQL queries
- Are porting existing ADO.NET code
- Working with very large datasets where query control is critical

**Use EF Core when you:**
- Want automatic migrations
- Need LINQ query composition
- Want change tracking and lazy loading
- Have complex relationships between entities
- Prefer code-first database design

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
SqliteWasmLogger.SetLogLevel(LogLevel.Warning);

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

## Recommended Pattern: Multi-View Instead of Multi-Tab

### Why Multiple Browser Tabs Don't Work with OPFS

OPFS (Origin Private File System) uses exclusive synchronous access handles — only one tab can hold a write lock on the database at a time. Opening multiple browser tabs creates multiple independent WASM runtimes, each trying to acquire exclusive access to the same database file. This is a fundamental browser API constraint, not a library limitation.

### The Solution: Floating Windows in a Single Tab

Instead of multiple browser tabs, use **multiple views within a single PWA tab**. This mirrors how desktop applications work — one process, one database connection, multiple windows on the same data.

```
Single Browser Tab (PWA)
├── View 1: Table View     ← Own DbContext per operation
├── View 2: Card View      ← Own DbContext per operation
├── View 3: Stats View     ← Own DbContext per operation
│
├── IDbContextFactory<T>   ← Creates short-lived contexts
└── DataNotifier           ← Broadcasts change events
```

### Implementation Pattern

**1. Use `IDbContextFactory<T>` for independent contexts per operation:**

```csharp
// Each view creates short-lived DbContext instances — no shared state conflicts
private async Task AddTodoAsync()
{
    await using var context = await DbContextFactory.CreateDbContextAsync();
    context.TodoItems.Add(newTodo);
    await context.SaveChangesAsync();

    // Notify other views that data changed
    Notifier.NotifyDataChanged();
}
```

**2. Use a lightweight notification service to synchronize views:**

```csharp
// Singleton service — broadcasts change events across all open views
public sealed class TodoDataNotifier
{
    public event Action? OnDataChanged;

    public void NotifyDataChanged()
    {
        OnDataChanged?.Invoke();
    }
}
```

**3. Each view subscribes and re-queries on change:**

```csharp
protected override void OnInitialized()
{
    Notifier.OnDataChanged += OnDataChanged;
}

private void OnDataChanged()
{
    _ = InvokeAsync(async () =>
    {
        await LoadDataAsync();   // Re-query with fresh DbContext
        StateHasChanged();
    });
}
```

This is the standard EF Core `IDbContextFactory` pattern — not a WASM workaround. It works the same way in ASP.NET, Blazor Server, and desktop applications. Each operation gets a short-lived `DbContext`, avoiding tracking conflicts and threading issues that arise from long-lived shared contexts.

The demo application includes a complete Multi-View example using `SqliteWasmBlazor.WindowHelper` — a lightweight Razor Class Library that adds draggable, resizable floating behavior to standard MudBlazor dialogs via JS interop. Navigate to `/multiview` in the demo to see it in action.

## Recommended Pattern: Data Initialization Without Page Reload

### The Anti-Pattern: Reload After Initial Data Fetch

A common mistake in OPFS-backed PWAs is this sequence on first launch:

```
1. App starts → acquires OPFS database lock
2. Database is empty → fetch data from remote API
3. Insert fetched data into SQLite
4. Full page reload (NavigationManager.NavigateTo("/", forceLoad: true))
5. App re-starts → tries to acquire OPFS lock again
```

On faster machines this may work because the lock is released before the reload completes. On slower machines, the reload races the lock release — the new runtime attempts to acquire the OPFS lock while the previous runtime is still tearing down. This results in the database being inaccessible, requiring a manual reload to recover.

This is a **fundamental architectural issue**, not a performance bug. A full page reload in a Blazor WASM PWA tears down the entire .NET runtime, the Web Worker, and the OPFS connection, then re-initializes everything from scratch. This is expensive (~100-200ms minimum) and inherently racy with OPFS lock cleanup.

### The Correct Pattern: Fetch, Insert, Refresh View

Never reload the page to display new data. Instead, update the view state in-place:

```csharp
// Correct: fetch, insert, refresh view — no reload
protected override async Task OnInitializedAsync()
{
    await using var context = await DbContextFactory.CreateDbContextAsync();

    var hasData = await context.TodoItems.AnyAsync();
    if (!hasData)
    {
        // Fetch from remote API
        var remoteData = await HttpClient.GetFromJsonAsync<List<TodoItemDto>>("api/todos");
        if (remoteData is not null)
        {
            // Insert into local SQLite
            foreach (var dto in remoteData)
            {
                context.TodoItems.Add(dto.ToEntity());
            }
            await context.SaveChangesAsync();
        }
    }

    // Load and display — no reload needed
    _todos = await context.TodoItems.OrderBy(t => t.UpdatedAt).ToListAsync();
}
```

```
Correct flow:
1. App starts → acquires OPFS lock (once)
2. Check if data exists
3. If empty → fetch from API, insert into SQLite
4. Load data into component state
5. StateHasChanged() → UI updates
```

### Why This Matters for PWAs

A Blazor WASM PWA is not a traditional web application where each page navigation is a cheap HTTP request. It is a **client-side application** with:

- A full .NET runtime loaded into memory
- A Web Worker managing OPFS file handles
- Exclusive database locks that require graceful teardown

Treating it like a server-rendered app (reload to refresh) breaks the single-runtime assumption that OPFS depends on. The correct mental model is a desktop application: initialize once, keep the process running, update the UI reactively.

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

## Release Notes

### Incremental Database Export/Import (Delta Sync)

File-based incremental export/import for large databases in offline-first PWAs. Export only changed items since last checkpoint, transfer the file manually (USB, cloud storage, etc.), and import with conflict resolution:

```csharp
// Export only changes since last checkpoint (delta export)
<MessagePackFileDownload T="TodoItemDto"
    GetPageAsync="@GetDeltaTodoItemsPageAsync"  // Only items modified since checkpoint
    GetTotalCountAsync="@GetDeltaCountAsync"
    FileName="@($"delta-{DateTime.Now:yyyyMMdd}.msgpack")"
    Mode="ExportMode.Delta" />  // Delta mode includes UpdatedAt/DeletedAt ranges

// Import with conflict resolution strategy
<MessagePackFileUpload T="TodoItemDto"
    OnBulkInsertAsync="@DeltaMergeTodoItemsAsync"  // Smart merge instead of replace
    Mode="ImportMode.Delta"
    ConflictResolution="ConflictResolutionStrategy.LastWriteWins" />  // Or LocalWins/DeltaWins
```

**Key Features:**

✅ **Automatic Checkpoint Management**
- Auto checkpoints created after every import/export operation
- Manual checkpoints with tombstone cleanup
- Checkpoint history with timestamp, description, and item counts
- Restore to any checkpoint with optional delta reapply

✅ **Efficient Delta Tracking**
- Only exports items modified since last checkpoint (`UpdatedAt > lastCheckpointTime`)
- Includes soft-deleted items (tombstones) for proper sync
- Pending delta count shows items awaiting export
- Significantly reduces data transfer for large databases

✅ **Three Conflict Resolution Strategies**
- **LastWriteWins** (default): Most recent `UpdatedAt` timestamp wins
- **LocalWins**: Local changes always preserved, imports only add new items
- **DeltaWins**: Imported changes always win, local items overwritten

✅ **Soft Delete (Tombstones)**
- Items marked with `IsDeleted` flag instead of hard deletion
- `DeletedAt` timestamp tracks deletion time for delta sync
- Tombstones included in delta export for proper deletion propagation
- Manual tombstone cleanup before creating manual checkpoints

**Architecture:**

```
Database Timeline:
├─ Checkpoint 1 (Manual)     ← Baseline: 100 active items, 0 tombstones
│  └─ Created 10 items       ← UpdatedAt = 2025-11-17 10:00
│  └─ Deleted 2 items        ← DeletedAt = 2025-11-17 10:05
├─ Delta Export              ← Exports 12 items (10 new + 2 deleted)
├─ Checkpoint 2 (Auto)       ← Auto checkpoint: 108 active, 2 tombstones
│  └─ Import 5 items         ← Conflict resolution applied
├─ Checkpoint 3 (Auto)       ← Auto checkpoint after import
│  └─ Created 3 items        ← UpdatedAt = 2025-11-17 10:30
├─ Pending Delta: 3 items    ← Awaiting next export
```

**Conflict Resolution Examples:**

```csharp
// LastWriteWins: Compare timestamps
Local:    UpdatedAt = 2025-11-17 10:00, Title = "Local Edit"
Imported: UpdatedAt = 2025-11-17 10:05, Title = "Remote Edit"
Result:   Title = "Remote Edit" (newer timestamp wins)

// LocalWins: Keep local changes
Local:    Title = "My Local Changes"
Imported: Title = "Remote Changes"
Result:   Title = "My Local Changes" (local always wins)

// DeltaWins: Always accept imported
Local:    Title = "Local Changes", UpdatedAt = 2025-11-17 10:05
Imported: Title = "Remote Changes", UpdatedAt = 2025-11-17 09:00 (older!)
Result:   Title = "Remote Changes" (delta wins despite older timestamp)
```

**Database Schema Requirements:**

```csharp
public class TodoItem
{
    public Guid Id { get; set; }
    public string Title { get; set; }
    public DateTime UpdatedAt { get; set; }        // Required for delta sync
    public bool IsDeleted { get; set; }            // Soft delete flag
    public DateTime? DeletedAt { get; set; }       // Deletion timestamp
}

public class SyncState  // Checkpoint tracking
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }        // Checkpoint timestamp
    public string Description { get; set; }
    public int ActiveItemCount { get; set; }
    public int TombstoneCount { get; set; }
    public string CheckpointType { get; set; }     // "Auto" or "Manual"
}
```

**Implementation Pattern:**

```csharp
// Delta export query
private async Task<(List<TodoItemDto> Items, int TotalCount)> GetDeltaTodoItemsPageAsync(
    int skip, int take)
{
    await using var context = await DbContextFactory.CreateDbContextAsync();

    // Get last checkpoint timestamp
    var lastCheckpoint = await context.SyncState
        .OrderByDescending(s => s.CreatedAt)
        .FirstOrDefaultAsync();

    var lastCheckpointTime = lastCheckpoint?.CreatedAt ?? DateTime.MinValue;

    // Query items modified since checkpoint (including soft-deleted)
    var query = context.TodoItems
        .Where(t =>
            (t.UpdatedAt > lastCheckpointTime && !t.IsDeleted) ||  // Modified items
            (t.IsDeleted && t.DeletedAt.HasValue && t.DeletedAt.Value > lastCheckpointTime))  // Deletions
        .OrderBy(t => t.UpdatedAt);

    var totalCount = await query.CountAsync();
    var items = await query
        .Skip(skip)
        .Take(take)
        .Select(t => t.ToDto())
        .ToListAsync();

    return (items, totalCount);
}

// Delta import with conflict resolution
private async Task DeltaMergeTodoItemsAsync(List<TodoItemDto> dtos)
{
    await using var context = await DbContextFactory.CreateDbContextAsync();

    foreach (var dto in dtos)
    {
        var existingItem = await context.TodoItems
            .FirstOrDefaultAsync(t => t.Id == dto.Id);

        if (existingItem is not null)
        {
            // Apply conflict resolution strategy
            var shouldUpdate = _conflictResolution switch
            {
                ConflictResolutionStrategy.LastWriteWins => dto.UpdatedAt > existingItem.UpdatedAt,
                ConflictResolutionStrategy.LocalWins => false,  // Never update
                ConflictResolutionStrategy.DeltaWins => true,   // Always update
                _ => throw new InvalidOperationException($"Unknown strategy: {_conflictResolution}")
            };

            if (shouldUpdate)
            {
                // Update existing item
                existingItem.Title = dto.Title;
                existingItem.UpdatedAt = dto.UpdatedAt;
                existingItem.IsDeleted = dto.IsDeleted;
                existingItem.DeletedAt = dto.DeletedAt;
            }
        }
        else
        {
            // Add new item
            context.TodoItems.Add(dto.ToEntity());
        }
    }

    await context.SaveChangesAsync();

    // Create auto checkpoint after import
    await context.CreateCheckpointAsync(
        $"Auto checkpoint after delta import ({dtos.Count} items)",
        "Auto");
}

// Checkpoint creation extension method
public static async Task<SyncState> CreateCheckpointAsync(
    this TodoDbContext context,
    string description,
    string checkpointType = "Auto",
    CancellationToken cancellationToken = default)
{
    var activeCount = await context.TodoItems
        .CountAsync(t => !t.IsDeleted, cancellationToken);

    var tombstoneCount = await context.TodoItems
        .CountAsync(t => t.IsDeleted, cancellationToken);

    var checkpoint = new SyncState
    {
        CreatedAt = DateTime.UtcNow,
        Description = description,
        ActiveItemCount = activeCount,
        TombstoneCount = tombstoneCount,
        CheckpointType = checkpointType
    };

    context.SyncState.Add(checkpoint);
    await context.SaveChangesAsync(cancellationToken);

    return checkpoint;
}
```

**What This Is:**

A file-based incremental backup/restore system for large databases. Useful when you need to:
- Transfer only changes between devices (vs. transferring entire database)
- Keep incremental backups with restore points
- Reduce file transfer size for large databases (100k+ records)
- Handle conflicts when merging changes from different sources

**What This Is NOT:**

This is **not** a real-time sync solution. It requires:
- Manual file transfer (download delta → copy file → upload delta on other device)
- No automatic sync between devices/users

For real-time/automatic sync, see the [Datasync TodoApp](https://github.com/b-straub/Datasync/tree/main/samples/todoapp-blazor-wasm-offline) sample which demonstrates proper offline-first synchronization patterns.

**Use Cases:**

- **Offline-First PWAs**: Export changes before going offline, import when back online
- **Multi-Device Transfer**: Manually share database state via file transfer
- **Incremental Backups**: Keep checkpoint history with smaller backup files
- **Data Migration**: Move data between environments with conflict handling

**Best Practices:**

1. Always store timestamps in UTC (`DateTime.UtcNow`)
2. Display timestamps in local time (`ToLocalTime()`)
3. Set `UpdatedAt` on every entity modification
4. Use soft delete for entities that need sync
5. Clean tombstones before manual checkpoints
6. Choose conflict resolution strategy based on use case:
   - **LastWriteWins**: Most recent edit wins (general purpose)
   - **LocalWins**: User's local edits are sacred (offline-first apps)
   - **DeltaWins**: Server/remote is source of truth (cloud sync)

**Future Direction:**

This foundation could be extended toward decentralized sync solutions, but currently it's a building block for offline-first scenarios, not a complete sync system.

See the Demo app's Administration and TodoImportExport components for complete implementation examples.

### Database Import/Export

Export and import your entire database with schema validation and efficient binary serialization:

```csharp
// Export database to MessagePack file
<MessagePackFileDownload T="TodoItemDto"
    GetPageAsync="@GetTodoItemsPageAsync"
    GetTotalCountAsync="@GetTodoItemCountAsync"
    FileName="@($"backup-{DateTime.Now:yyyyMMdd}.msgpack")"
    SchemaVersion="1.0"
    AppIdentifier="MyApp" />

// Import database with validation
<MessagePackFileUpload T="TodoItemDto"
    OnBulkInsertAsync="@BulkInsertTodoItemsAsync"
    ExpectedSchemaVersion="1.0"
    ExpectedAppIdentifier="MyApp" />
```

**Features:**
- ✅ **Schema Validation** - Prevents importing incompatible data with version and app identifier checks
- ✅ **Efficient Serialization** - MessagePack binary format (60% smaller than JSON)
- ✅ **Streaming Export** - Handles large datasets with pagination (tested with 100k+ records)
- ✅ **Bulk Import** - Optimized SQL batching respects SQLite's 999 parameter limit
- ✅ **Progress Tracking** - Real-time progress updates during import/export operations
- ✅ **Type Safety** - Full DTO validation ensures data integrity

Perfect for:
- Database backups and restores
- Data migration between environments
- Sharing datasets between users
- Offline-first PWA scenarios

**How it works:**
Export streams data in MessagePack format with a file header (magic number "SWBMP", schema version, type info, record count) followed by serialized items. Import deserializes the stream in batches, validates the header, and uses raw SQL INSERT statements to preserve entity IDs while respecting SQLite's 999 parameter limit (166 rows per batch for 6-column entities). The header-first approach ensures schema compatibility before processing begins, preventing partial imports of incompatible data.

**Why sqlite-wasm needed patching:**
The official sqlite-wasm OPFS SAHPool VFS lacked a `renameFile()` implementation. The patch (`patches/@sqlite.org+sqlite-wasm+3.50.4-build1.patch`) adds this method to enable efficient database renaming by updating the SAH (Synchronous Access Handle) metadata mapping with the new path while keeping the physical file intact - avoiding expensive file copying for large databases.

See the Demo app's TodoImportExport component for a complete implementation example.

### Version 0.6.7-pre (2025-11-14)

**Log Level Configuration Change**

The `SqliteWasmConnection` constructor now uses the standard `Microsoft.Extensions.Logging.LogLevel` enum instead of the custom `SqliteWasmLogLevel`:

```csharp
// ❌ Old (0.6.6-pre and earlier)
var connection = new SqliteWasmConnection("Data Source=MyDb.db", SqliteWasmLogLevel.Warning);

// ✅ New (0.6.7-pre and later)
using Microsoft.Extensions.Logging; // Add this using

// Default is LogLevel.Warning, so you can omit it:
var connection = new SqliteWasmConnection("Data Source=MyDb.db");

// Or specify a different level:
var connection = new SqliteWasmConnection("Data Source=MyDb.db", LogLevel.Error);
```

**Migration:** Simply add `using Microsoft.Extensions.Logging;` and change `SqliteWasmLogLevel` to `LogLevel`. If you were using the default `Warning` level, you can omit the parameter entirely.

Available log levels: `Trace`, `Debug`, `Information`, `Warning` (default), `Error`, `Critical`, `None`

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
