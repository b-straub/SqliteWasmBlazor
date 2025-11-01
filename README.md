# SQLiteNET.Opfs - OPFS-Backed SQLite for Blazor WASM

A .NET 10 Razor Class Library that provides persistent, high-performance SQLite storage for Blazor WebAssembly applications using OPFS (Origin Private File System).

## Overview

This library combines the power of SQLite WASM with OPFS to provide a **drop-in replacement** for EF Core's InMemory database provider, giving you true persistence that survives page refreshes.

## Features

- ✅ **Persistent Storage** - Data survives page refreshes and browser restarts
- ✅ **High Performance** - OPFS provides near-native file system performance
- ✅ **EF Core Compatible** - Works seamlessly with Entity Framework Core
- ✅ **Drop-in Replacement** - Easy migration from InMemory provider
- ✅ **Official SQLite WASM** - Uses the official SQLite 3.50.4 WASM build
- ✅ **OPFS SAHPool VFS** - Optimized for single-connection scenarios
- ✅ **No Special Headers Required** - SAHPool doesn't need COOP/COEP headers

## Browser Support

| Browser | Version | OPFS Support |
|---------|---------|--------------|
| Chrome  | 108+    | ✅ Full      |
| Edge    | 108+    | ✅ Full      |
| Firefox | 111+    | ✅ Full      |
| Safari  | 16.4+   | ✅ Full      |

## Project Structure

```
SQLiteNET/
├── SQLiteNET.Opfs/                  # Razor Class Library
│   ├── Abstractions/
│   │   └── IOpfsStorage.cs          # Storage abstraction
│   ├── Components/
│   │   └── OpfsInitializer.razor    # Optional initialization component
│   ├── Extensions/
│   │   └── OpfsDbContextExtensions.cs # EF Core integration
│   ├── Services/
│   │   └── OpfsStorageService.cs    # OPFS storage implementation
│   └── wwwroot/js/
│       ├── sqlite3.wasm              # SQLite WASM binary (836 KB)
│       ├── sqlite3-bundler-friendly.mjs # SQLite JS module (383 KB)
│       ├── sqlite3-opfs-async-proxy.js # OPFS async proxy (20 KB)
│       └── sqlite-opfs-initializer.js  # OPFS initialization module
│
└── SQLiteNET.Opfs.Demo/             # Sample Blazor WASM App
    ├── Data/
    │   └── TodoDbContext.cs          # EF Core DbContext
    ├── Models/
    │   └── TodoItem.cs               # Entity model
    └── Pages/
        └── TodoList.razor            # Demo page with CRUD operations
```

## Installation

### 1. Add Project Reference

```bash
dotnet add reference path/to/SQLiteNET.Opfs/SQLiteNET.Opfs.csproj
```

### 2. Configure Services (Program.cs)

Replace your InMemory database registration:

**Before (InMemory):**
```csharp
services.AddDbContext<MyDbContext>(options =>
    options.UseInMemoryDatabase("MyDatabase"));
```

**After (OPFS):**
```csharp
using SQLiteNET.Opfs.Extensions;

// Add OPFS-backed SQLite DbContext
builder.Services.AddOpfsSqliteDbContext<MyDbContext>();

var host = builder.Build();

// Initialize OPFS
await host.Services.InitializeOpfsAsync();

// Configure and create database
using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MyDbContext>();
    await dbContext.ConfigureSqliteForWasmAsync();
    await dbContext.Database.EnsureCreatedAsync();
}

await host.RunAsync();
```

### 3. Create Your DbContext

```csharp
using Microsoft.EntityFrameworkCore;

public class MyDbContext : DbContext
{
    public MyDbContext(DbContextOptions<MyDbContext> options) : base(options)
    {
    }

    public DbSet<MyEntity> MyEntities { get; set; }
}
```

### 4. Use It!

```csharp
@inject MyDbContext DbContext

@code {
    protected override async Task OnInitializedAsync()
    {
        var items = await DbContext.MyEntities.ToListAsync();
    }

    private async Task AddItem(MyEntity item)
    {
        DbContext.MyEntities.Add(item);
        await DbContext.SaveChangesAsync();
    }
}
```

## How It Works

### Architecture

```
Blazor WASM App
    ↓
EF Core DbContext
    ↓
SQLite Provider (UseSqlite)
    ↓
SQLite WASM Engine
    ↓
OPFS SAHPool VFS
    ↓
Browser OPFS (Persistent Storage)
```

### Key Components

1. **OpfsStorageService** - C# service that manages JavaScript interop with SQLite WASM
2. **sqlite-opfs-initializer.js** - JavaScript module that initializes OPFS SAHPool VFS
3. **SQLite WASM** - Official SQLite 3.50.4 compiled to WebAssembly
4. **OPFS SAHPool VFS** - Virtual File System using SharedAccessHandles for optimal performance

### OPFS vs InMemory vs Cache Storage

| Feature | InMemory | Cache Storage (Besql) | OPFS (This Library) |
|---------|----------|----------------------|---------------------|
| Persistence | ❌ None | ✅ Yes | ✅ Yes |
| Performance | 🔥 Fastest | ⚡ Good | ⚡ Very Fast |
| Startup | Instant | ~50ms | ~100ms |
| File System | ❌ No | Emulated | ✅ Native |
| Browser Support | All | All | Chrome 108+, FF 111+, Safari 16.4+ |
| Storage Type | RAM | IndexedDB-backed Cache | OPFS FileSystem API |
| Quota | RAM limited | 5-10 MB typical | Origin quota (generous) |

## Migration from WebAppBase InMemory

If you're using WebAppBase with `AddWebAppBaseInMemoryDatabase<T>()`:

### Before:
```csharp
builder.Services.AddWebAppBaseInMemoryDatabase<ToDoDBContext>(apiService);
```

### After:
```csharp
builder.Services.AddOpfsSqliteDbContext<ToDoDBContext>();

// ... later in startup
await host.Services.InitializeOpfsAsync();
```

### What Stays the Same:
- ✅ All entity models
- ✅ All DbContext operations
- ✅ All LINQ queries
- ✅ SaveChanges patterns
- ✅ Existing sync logic

### What Changes:
- ❌ Remove `InMemoryDatabaseRoot` singleton
- ➕ Add OPFS initialization in Program.cs
- ➕ Call `ConfigureSqliteForWasmAsync()` before EnsureCreated/Migrate

## Running the Demo

```bash
cd SQLiteNET.Opfs.Demo
dotnet run
```

Navigate to `/todos` to see the OPFS-backed Todo List with full CRUD operations.

### Testing Persistence

1. Add some todo items
2. Refresh the page (F5)
3. ✅ Your data is still there!

This demonstrates true persistence that InMemory databases don't provide.

## API Reference

### Extension Methods

#### `AddOpfsSqliteDbContext<TContext>()`
Registers a DbContext with OPFS-backed SQLite storage.

```csharp
builder.Services.AddOpfsSqliteDbContext<MyDbContext>();
```

#### `InitializeOpfsAsync()`
Initializes OPFS storage. Call after building the host.

```csharp
var host = builder.Build();
await host.Services.InitializeOpfsAsync();
```

#### `ConfigureSqliteForWasmAsync()`
Configures SQLite journal mode for WASM compatibility. Call before EnsureCreated/Migrate.

```csharp
await dbContext.ConfigureSqliteForWasmAsync();
```

### IOpfsStorage Interface

```csharp
public interface IOpfsStorage
{
    Task<bool> InitializeAsync();
    bool IsReady { get; }
    Task<string[]> GetFileListAsync();
    Task<byte[]> ExportDatabaseAsync(string filename);
    Task<int> ImportDatabaseAsync(string filename, byte[] data);
    Task<int> GetCapacityAsync();
    Task<int> AddCapacityAsync(int count);
}
```

## Advanced Usage

### Export Database

```csharp
@inject IOpfsStorage OpfsStorage

var data = await OpfsStorage.ExportDatabaseAsync("MyDbContext.db");
// Save data to server, download, etc.
```

### Import Database

```csharp
var data = await httpClient.GetByteArrayAsync("backup.db");
await OpfsStorage.ImportDatabaseAsync("MyDbContext.db", data);
```

### Check Storage Status

```csharp
var files = await OpfsStorage.GetFileListAsync();
var capacity = await OpfsStorage.GetCapacityAsync();
```

## Configuration

### Database Naming

By default, databases are named `{DbContextName}.db`. You can customize this:

```csharp
builder.Services.AddDbContext<MyDbContext>((provider, options) =>
{
    options.UseSqlite("Data Source=custom-name.db");
});
```

### SAH Pool Capacity

The default capacity is 6 SharedAccessHandles. Increase if needed:

```csharp
// In sqlite-opfs-initializer.js
poolUtil = await sqlite3.installOpfsSAHPoolVfs({
    initialCapacity: 12,  // Increase for more concurrent operations
    directory: '/databases',
    name: 'opfs-sahpool'
});
```

## Troubleshooting

### "OPFS not initialized" Error

Ensure you call `InitializeOpfsAsync()` after building the host:

```csharp
var host = builder.Build();
await host.Services.InitializeOpfsAsync();  // Must be called!
await host.RunAsync();
```

### "Cannot open database" Error

Make sure you configure SQLite for WASM before creating the database:

```csharp
await dbContext.ConfigureSqliteForWasmAsync();
await dbContext.Database.EnsureCreatedAsync();
```

### Browser Compatibility

OPFS requires modern browsers. Check `IOpfsStorage.IsReady` to verify availability:

```csharp
if (!OpfsStorage.IsReady)
{
    // Show fallback UI or error message
}
```

## Performance Tips

1. **Use Async Methods** - Always use `ToListAsync()`, `SaveChangesAsync()`, etc.
2. **Batch Operations** - Group multiple operations in a single transaction
3. **Index Frequently Queried Columns** - SQLite supports full indexing
4. **Use Projections** - Select only needed columns to reduce memory usage

## Technical Details

### SQLite Configuration

The library automatically configures SQLite for WASM:

```sql
PRAGMA journal_mode=DELETE;  -- WASM doesn't support WAL properly
PRAGMA synchronous=NORMAL;    -- Balance between safety and performance
PRAGMA temp_store=MEMORY;     -- Use memory for temp tables
```

### OPFS SAHPool VFS

Uses SQLite's OPFS SAHPool (SharedAccessHandle Pool) VFS:
- Single exclusive connection per database
- Pre-allocated synchronous access handles
- No COOP/COEP headers required
- Optimal for Blazor WASM single-thread model

## Credits

- **SQLite WASM** - Official SQLite 3.50.4 WASM build from https://sqlite.org
- **OPFS** - W3C File System API for origin-private storage
- **Inspiration** - Besql (bitplatform) for EF Core WASM patterns

## License

MIT License - Feel free to use in your projects!

## Contributing

Issues and PRs welcome! Please test thoroughly with different browsers.

---

**Built with ❤️ for the Blazor WASM community**
