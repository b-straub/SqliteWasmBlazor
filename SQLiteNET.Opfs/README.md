# SQLiteNET.Opfs

High-performance SQLite persistence for Blazor WebAssembly using OPFS (Origin Private File System) with incremental sync optimization.

## Features

✅ **Incremental Sync** - VFS-tracked dirty pages (only modified data is persisted)
✅ **JSImport Optimization** - Zero-copy data transfers between C# and JavaScript
✅ **Automatic TypeScript Build** - MSBuild integration compiles TypeScript during build
✅ **Entity Framework Core Support** - Seamless integration with EF Core SQLite
✅ **Web Worker Architecture** - Non-blocking OPFS operations
✅ **Graceful Fallback** - Automatic fallback to full sync when incremental unavailable
✅ **Browser Persistence** - Data survives page reloads and browser restarts

## Performance

### Incremental Sync Performance
- **Full Sync**: ~200-400ms for 10MB database (entire file written)
- **Incremental Sync**: ~20-40ms for typical changes (only dirty pages written)
- **JSImport Optimization**: ~100-150ms saved per operation (zero-copy transfers)

### Example: 10MB Database, 100KB of Changes
- **Full Sync**: 10MB written → ~300ms
- **Incremental Sync**: 100KB written → ~30ms (~10x faster)

## Architecture

### Components

1. **Native VFS Tracking** (C) - SQLite VFS wrapper that tracks dirty pages at write time
2. **C# Interop Layer** - P/Invoke wrappers for VFS tracking + JSImport for OPFS operations
3. **TypeScript Worker** - OPFS operations in Web Worker (non-blocking)
4. **Storage Service** - Orchestrates persistence with automatic fallback

### Data Flow (Incremental Sync)

```
SQLite Write Operation
  ↓
VFS Tracking (marks page dirty in bitmap)
  ↓
SaveChangesAsync() called
  ↓
VfsInterop.GetDirtyPages() → [2, 5, 8, 12]
  ↓
OpfsJSInterop.ReadPagesFromMemfs() (synchronous, zero-copy)
  ↓
OpfsJSInterop.PersistDirtyPagesAsync() (via Web Worker)
  ↓
OPFS partial write (only dirty pages)
  ↓
VfsInterop.ResetDirty()
```

## Installation

### Prerequisites

- .NET 10.0 (or .NET 8.0+)
- Emscripten SDK 3.1.56 (for building custom SQLite library)
- Node.js and npm (for TypeScript compilation)

### 1. Add Package Reference

```xml
<ItemGroup>
  <ProjectReference Include="..\SQLiteNET.Opfs\SQLiteNET.Opfs.csproj" />
</ItemGroup>
```

### 2. Configure Native Build

**Your Blazor App .csproj**:
```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <WasmBuildNative>true</WasmBuildNative>
</PropertyGroup>

<ItemGroup>
  <!-- Exclude packaged SQLite library - using custom build with VFS tracking -->
  <PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" Version="2.1.11"
                    ExcludeAssets="native;buildTransitive" />
</ItemGroup>

<!-- Use custom SQLite library with VFS tracking -->
<ItemGroup>
  <NativeFileReference Include="..\SQLiteNET.Opfs\Native\lib\e_sqlite3.a" />
</ItemGroup>
```

### 3. Build Custom SQLite Library

**Install Emscripten** (if not already installed):
```bash
git clone https://github.com/emscripten-core/emsdk.git
cd emsdk
./emsdk install 3.1.56
./emsdk activate 3.1.56
source ./emsdk_env.sh  # or emsdk_env.bat on Windows
```

**Build VFS-Tracked SQLite**:
```bash
cd SQLiteNET.Opfs/Native
./build_sqlite.sh
```

This generates `lib/e_sqlite3.a` with VFS tracking enabled.

### 4. Register Service

**Program.cs**:
```csharp
using SQLiteNET.Opfs.Abstractions;
using SQLiteNET.Opfs.Services;

builder.Services.AddScoped<IOpfsStorage, OpfsStorageService>();
```

## Usage

### 1. Create DbContext with OPFS Support

```csharp
public class AppDbContext : DbContext
{
    private readonly IOpfsStorage _opfsStorage;

    public AppDbContext(IOpfsStorage opfsStorage)
    {
        _opfsStorage = opfsStorage;
    }

    public DbSet<TodoItem> Todos => Set<TodoItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=app.db");
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        int result = await base.SaveChangesAsync(cancellationToken);

        // Persist to OPFS after successful save
        // (automatically uses incremental sync if available)
        await _opfsStorage.Persist("app.db");

        return result;
    }
}
```

### 2. Initialize OPFS on Startup

```csharp
@page "/"
@inject IOpfsStorage OpfsStorage
@inject AppDbContext DbContext

protected override async Task OnInitializedAsync()
{
    // Initialize OPFS worker
    bool initialized = await OpfsStorage.InitializeAsync();

    if (!initialized)
    {
        Console.WriteLine("⚠ OPFS initialization failed");
        return;
    }

    // Check if incremental sync is available
    if (OpfsStorage.IsIncrementalSyncEnabled)
    {
        Console.WriteLine("✓ Incremental sync enabled (VFS tracking active)");
    }
    else
    {
        Console.WriteLine("⚠ Using full sync (VFS tracking unavailable)");
    }

    // Load database from OPFS to MEMFS
    await OpfsStorage.Load("app.db");

    // Apply migrations
    await DbContext.Database.MigrateAsync();
}
```

### 3. Use Entity Framework Normally

```csharp
// Add
var todo = new TodoItem { Title = "Buy milk", IsComplete = false };
DbContext.Todos.Add(todo);
await DbContext.SaveChangesAsync();  // Automatically persists to OPFS

// Query
var todos = await DbContext.Todos.Where(t => !t.IsComplete).ToListAsync();

// Update
todo.IsComplete = true;
await DbContext.SaveChangesAsync();  // Only dirty pages written to OPFS

// Delete
DbContext.Todos.Remove(todo);
await DbContext.SaveChangesAsync();
```

## Advanced Usage

### Performance Testing: Force Full Sync

Temporarily disable incremental sync for performance comparison:

```csharp
protected override async Task OnInitializedAsync()
{
    await OpfsStorage.InitializeAsync();

    // Disable incremental sync (for testing)
    OpfsStorage.ForceFullSync = true;

    Console.WriteLine($"Incremental sync: {!OpfsStorage.ForceFullSync}");
}
```

### Bulk Operations: Pause/Resume Persistence

Avoid multiple persist operations during bulk updates:

```csharp
public async Task BulkImport(List<TodoItem> items)
{
    // Pause automatic persistence
    OpfsStorage.PauseAutomaticPersistent();

    try
    {
        foreach (var item in items)
        {
            DbContext.Todos.Add(item);
        }

        // Save all changes (no OPFS write yet)
        await DbContext.SaveChangesAsync();
    }
    finally
    {
        // Resume and persist all accumulated changes at once
        await OpfsStorage.ResumeAutomaticPersistent();
    }
}
```

### Export/Import Database

```csharp
// Export database to byte array
byte[] dbData = await OpfsStorage.ExportDatabaseAsync("app.db");

// Save to file (example: download)
using var stream = new MemoryStream(dbData);
// ... trigger browser download

// Import database from byte array
await OpfsStorage.ImportDatabaseAsync("app.db", dbData);
```

### List Files in OPFS

```csharp
string[] files = await OpfsStorage.GetFileListAsync();
foreach (var file in files)
{
    Console.WriteLine($"OPFS file: {file}");
}
```

## Project Structure

```
SQLiteNET.Opfs/
├── Abstractions/
│   └── IOpfsStorage.cs          # Public API interface
├── Services/
│   └── OpfsStorageService.cs    # Main service implementation
├── Interop/
│   ├── VfsInterop.cs            # P/Invoke for native VFS tracking
│   └── OpfsJSInterop.cs         # JSImport for OPFS operations
├── Native/
│   ├── src/
│   │   ├── vfs_tracking.h       # VFS tracking header
│   │   └── vfs_tracking.c       # VFS tracking implementation
│   ├── build_sqlite.sh          # Build script for custom SQLite
│   └── lib/
│       └── e_sqlite3.a          # Custom SQLite library (generated)
├── Typescript/
│   ├── opfs-worker.ts           # OPFS Web Worker
│   ├── opfs-initializer.ts     # Worker initialization
│   ├── opfs-interop.ts          # JSImport exports
│   └── package.json             # Build configuration
├── wwwroot/
│   ├── opfs-worker.js           # Compiled worker (generated)
│   └── opfs-interop.js          # Compiled interop (generated)
└── Components/
    └── OpfsInitializer.razor.js # Compiled initializer (generated)
```

## Build Process

### Automatic TypeScript Compilation

TypeScript is automatically compiled during `dotnet build` via MSBuild targets:

```xml
<Target Name="CompileTypeScript" BeforeTargets="BeforeBuild">
  <Message Importance="high" Text="[TypeScript] Checking TypeScript compilation..." />

  <!-- Install npm packages if needed -->
  <Exec Condition="'$(TypeScriptNodeModulesExists)' != 'true'"
        Command="npm install"
        WorkingDirectory="$(TypeScriptDir)" />

  <!-- Compile TypeScript -->
  <Exec Command="npm run build"
        WorkingDirectory="$(TypeScriptDir)" />
</Target>
```

**Output**:
- `wwwroot/opfs-worker.js` (66.5kb) - Web Worker bundle
- `Components/OpfsInitializer.razor.js` (23.3kb) - Main thread module
- `wwwroot/opfs-interop.js` (9.3kb) - JSImport module

### Manual TypeScript Build

If you need to build TypeScript independently:

```bash
cd Typescript
npm install
npm run build
```

## Troubleshooting

### ⚠ VFS Tracking Not Available

**Console**: `[OpfsStorageService] ⚠ VFS tracking unavailable`

**Causes**:
1. Custom SQLite library not built
2. Wrong Emscripten version (must match .NET runtime: 3.1.56 for .NET 10)
3. `NativeFileReference` not configured in consuming app

**Solution**:
```bash
cd SQLiteNET.Opfs/Native
./build_sqlite.sh
cd ../../YourApp
dotnet clean
dotnet build
```

### ⚠ No Dirty Pages Detected

**Console**: `[OpfsStorageService] No dirty pages for X.db, skipping persist`

**Causes**:
1. VFS not registered as default
2. Database opened before VFS initialization

**Solution**:
- Initialize OPFS before creating DbContext
- Verify VFS registration in build output

### ⚠ Worker Re-initialization Error

**Console**: `[OPFS Worker] SAHPool initialization failed`

**Cause**: Multiple worker instances trying to access OPFS

**Solution**: Ensure single worker instance via global `__opfsSendMessage` (fixed in current version)

### ⚠ TypeScript Not Compiling

**Symptom**: Old JavaScript still running after TypeScript changes

**Solution**:
```bash
cd Typescript
npm run build
cd ..
dotnet clean
dotnet build
```

Hard refresh browser (Ctrl+Shift+R or Cmd+Shift+R)

## Performance Optimization Tips

### 1. Batch Database Operations

```csharp
// ❌ Bad: Multiple persist calls
foreach (var item in items)
{
    DbContext.Add(item);
    await DbContext.SaveChangesAsync(); // Persist after each
}

// ✅ Good: Single persist after batch
DbContext.AddRange(items);
await DbContext.SaveChangesAsync(); // Persist once
```

### 2. Monitor Dirty Pages

```csharp
if (OpfsStorage.IsIncrementalSyncEnabled)
{
    // Get dirty page count without persisting
    int rc = VfsInterop.GetDirtyPages("app.db", out uint pageCount, out IntPtr pagesPtr);
    VfsInterop.FreePages(pagesPtr);

    long dirtyKB = (pageCount * 4096) / 1024;
    Console.WriteLine($"Dirty data: {dirtyKB} KB ({pageCount} pages)");
}
```

### 3. Measure Performance

```csharp
var sw = Stopwatch.StartNew();
await DbContext.SaveChangesAsync();
sw.Stop();

Console.WriteLine($"Persist time: {sw.ElapsedMilliseconds}ms");
```

## Browser Compatibility

| Browser | OPFS | SAHPool | Status |
|---------|------|---------|--------|
| Chrome 108+ | ✅ | ✅ | Fully supported |
| Edge 108+ | ✅ | ✅ | Fully supported |
| Firefox 111+ | ✅ | ✅ | Fully supported |
| Safari 15.2+ | ✅ | ⚠️ | OPFS available, SAHPool varies |

## Technical Details

### Memory Overhead

For database of size `S` bytes with 4KB page size:
- **Bitmap size**: `(S / 4096) / 8` bytes
- **Example**: 10MB database = 320 bytes overhead (~0.003%)

### VFS Tracking Details

See [INCREMENTAL-SYNC.md](../SQLiteNET.Opfs.Demo/INCREMENTAL-SYNC.md) for detailed VFS implementation docs.

### JSImport Optimization

See [JSIMPORT-WORKER-FIX.md](../SQLiteNET.Opfs.Demo/JSIMPORT-WORKER-FIX.md) for worker architecture and zero-copy transfer details.

## Limitations

1. **Browser Storage Quotas** - OPFS subject to browser storage limits (typically 60% of available disk)
2. **Single Origin** - Data isolated per origin, cannot share between domains
3. **No Server Sync** - OPFS is local-only, no built-in cloud sync
4. **Browser-Specific** - OPFS API not available in Node.js or server environments

## License

MIT License - see [LICENSE](LICENSE) for details.

## Credits

Based on:
- [SQLite](https://www.sqlite.org/) (public domain)
- [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) by Eric Sink
- [OPFS SAHPool](https://sqlite.org/wasm/doc/trunk/persistence.md#sahpool) implementation from sqlite-wasm

## References

- [INCREMENTAL-SYNC.md](../SQLiteNET.Opfs.Demo/INCREMENTAL-SYNC.md) - Detailed VFS tracking documentation
- [JSIMPORT-ANALYSIS.md](../SQLiteNET.Opfs.Demo/JSIMPORT-ANALYSIS.md) - JSImport performance analysis
- [JSIMPORT-WORKER-FIX.md](../SQLiteNET.Opfs.Demo/JSIMPORT-WORKER-FIX.md) - Worker architecture details
