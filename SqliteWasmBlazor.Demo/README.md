# SQLiteNET.Opfs.Demo

Demo Blazor WebAssembly application showcasing high-performance SQLite persistence with OPFS (Origin Private File System) and incremental sync.

## Live Demo Features

✅ **Todo List Application** - Simple CRUD operations with EF Core
✅ **Incremental Sync** - Only dirty pages persisted to OPFS
✅ **JSImport Optimization** - Zero-copy data transfers
✅ **Performance Metrics** - Real-time persistence timing
✅ **Bulk Operations** - Test with 50,000+ entries
✅ **Browser Persistence** - Data survives page reloads

## Quick Start

### 1. Build Custom SQLite Library

```bash
# Install Emscripten SDK (if not already installed)
git clone https://github.com/emscripten-core/emsdk.git
cd emsdk
./emsdk install 3.1.56
./emsdk activate 3.1.56
source ./emsdk_env.sh

# Build custom SQLite with VFS tracking
cd ../SQLiteNET.Opfs/Native
./build_sqlite.sh
```

### 2. Run Demo

```bash
cd SQLiteNET.Opfs.Demo
dotnet run
```

Navigate to `https://localhost:5001`

## Demo Application

### Todo List Page

The main page (`/todolist`) demonstrates all features:

**Operations**:
- ➕ Add Todo - Single insert with persist
- ✏️ Edit Todo - Update with incremental sync
- ✅ Toggle Complete - Fast update (only dirty pages)
- 🗑️ Delete Todo - Remove with persist
- 📦 Add 50,000 Todos - Bulk insert performance test
- 🗂️ Delete All - Clear database

**UI Features**:
- Real-time todo count
- Performance timing for persist operations
- Console logs showing:
  - Dirty page count
  - Bytes written
  - Incremental vs full sync
  - VFS tracking status

### Console Output Example

```
[OpfsStorageService] ✓ OPFS initialized: OPFS Worker initialized successfully
[OpfsStorageService] ✓ Capacity: 10, Files: 1
[OpfsStorageService] ✓ JSImport interop initialized
[OpfsStorageService] ✓ VFS tracking initialized (page size: 4096 bytes)

[VFS Tracking] Found 2 dirty pages for 'TodoDb.db'
[OpfsStorageService] Persisting (incremental): TodoDb.db - 2 dirty pages
[OpfsStorageService] ✓ JSImport: Written 2 pages (8 KB)
[OpfsStorageService] ✓ Persisted 2 pages (8 KB)
```

## Architecture

### DbContext Implementation

**TodoDbContext.cs**:
```csharp
public class TodoDbContext : DbContext
{
    private readonly IOpfsStorage _opfsStorage;

    public TodoDbContext(IOpfsStorage opfsStorage)
    {
        _opfsStorage = opfsStorage;
    }

    public DbSet<TodoItem> Todos => Set<TodoItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=TodoDb.db");
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        int result = await base.SaveChangesAsync(cancellationToken);

        // Automatically persist to OPFS
        // (incremental sync used if available)
        await _opfsStorage.Persist("TodoDb.db");

        return result;
    }
}
```

### Page Initialization

**TodoList.razor**:
```csharp
protected override async Task OnInitializedAsync()
{
    // Initialize OPFS worker
    bool initialized = await OpfsStorage.InitializeAsync();

    if (!initialized)
    {
        Console.WriteLine("❌ OPFS initialization failed");
        return;
    }

    // Check incremental sync status
    Console.WriteLine($"Incremental sync: {OpfsStorage.IsIncrementalSyncEnabled}");

    // Load database from OPFS to MEMFS
    await OpfsStorage.Load("TodoDb.db");

    // Apply migrations
    await DbContext.Database.MigrateAsync();

    // Load todos
    await UpdateTotalCountAsync();
}
```

## Performance Testing

### Test 1: Single Todo Add/Update/Delete

**Incremental Sync** (Typical):
- Add: ~20-40ms (2-3 dirty pages)
- Update: ~15-30ms (1-2 dirty pages)
- Delete: ~10-20ms (1 dirty page)

**Full Sync** (ForceFullSync = true):
- Add: ~150-300ms (entire database)
- Update: ~150-300ms
- Delete: ~150-300ms

### Test 2: Bulk Insert (50,000 Todos)

**With Incremental Sync**:
```
Initial insert: ~2-3 seconds (entire DB is new)
  → All pages dirty: ~12,500 pages @ 4KB = ~50MB
```

**With Full Sync**:
```
Each save: ~200-400ms × 50,000 = ~5-8 hours (impractical)
```

**With Pause/Resume** (Recommended):
```csharp
OpfsStorage.PauseAutomaticPersistent();
for (int i = 0; i < 50000; i++)
{
    DbContext.Todos.Add(new TodoItem { Title = $"Todo {i}" });
}
await DbContext.SaveChangesAsync();  // No OPFS write
await OpfsStorage.ResumeAutomaticPersistent();  // Single persist

Total time: ~3-5 seconds
```

### Test 3: Toggle Complete (Update Existing)

**Incremental Sync**:
```
Update single todo: ~15-25ms
  → Dirty pages: 1-2 (only affected pages)
  → Bytes written: ~8KB
```

**Full Sync**:
```
Update single todo: ~200-350ms
  → Full database: ~50MB (for 50k todos)
```

**Speedup**: ~15x faster with incremental sync

## Project Configuration

### SQLiteNET.Opfs.Demo.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <WasmBuildNative>true</WasmBuildNative>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.0-rc.2.25502.107" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0-rc.2.25502.107" />
    <PackageReference Include="MudBlazor" Version="8.6.0" />

    <!-- Exclude packaged SQLite library - using custom build -->
    <PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" Version="2.1.11"
                      ExcludeAssets="native;buildTransitive" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SQLiteNET.Opfs\SQLiteNET.Opfs.csproj" />
  </ItemGroup>

  <!-- Custom SQLite library with VFS tracking -->
  <ItemGroup>
    <NativeFileReference Include="..\SQLiteNET.Opfs\Native\lib\e_sqlite3.a" />
  </ItemGroup>
</Project>
```

### Program.cs

```csharp
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SQLiteNET.Opfs.Abstractions;
using SQLiteNET.Opfs.Demo;
using SQLiteNET.Opfs.Demo.Data;
using SQLiteNET.Opfs.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register OPFS storage service
builder.Services.AddScoped<IOpfsStorage, OpfsStorageService>();

// Register EF Core DbContext
builder.Services.AddDbContext<TodoDbContext>();

// MudBlazor
builder.Services.AddMudServices();

await builder.Build().RunAsync();
```

## Troubleshooting

### Database Not Persisting

**Symptom**: Todos disappear after page reload

**Check**:
1. Browser console for OPFS errors
2. `OpfsStorage.IsReady` is true
3. `SaveChangesAsync()` calls `Persist()`

**Solution**:
```csharp
protected override async Task OnInitializedAsync()
{
    bool initialized = await OpfsStorage.InitializeAsync();
    if (!initialized)
    {
        Console.WriteLine("❌ OPFS failed to initialize");
        // Check browser compatibility
    }
}
```

### Slow Performance

**Symptom**: Persist operations take >100ms

**Check**:
```csharp
Console.WriteLine($"Incremental sync enabled: {OpfsStorage.IsIncrementalSyncEnabled}");
Console.WriteLine($"Force full sync: {OpfsStorage.ForceFullSync}");
```

**Causes**:
1. VFS tracking not available (using full sync)
2. `ForceFullSync = true` (testing mode)
3. Large database with many dirty pages

**Solution**:
- Verify custom SQLite library is built and referenced
- Check console for VFS initialization messages
- Use pause/resume for bulk operations

### TypeScript/Worker Errors

**Symptom**: `[OPFS Worker] initialization failed`

**Check**:
1. TypeScript compiled successfully during build
2. `opfs-worker.js` exists in `wwwroot`
3. Browser supports OPFS

**Solution**:
```bash
cd ../SQLiteNET.Opfs/Typescript
npm install
npm run build
cd ../../SQLiteNET.Opfs.Demo
dotnet clean
dotnet build
```

### VFS Tracking Not Available

**Console**: `[OpfsStorageService] ⚠ VFS tracking unavailable`

**Cause**: Custom SQLite library not loaded

**Solution**:
```bash
# Rebuild custom library
cd ../SQLiteNET.Opfs/Native
./build_sqlite.sh

# Verify output
ls -lh lib/e_sqlite3.a

# Clean and rebuild demo
cd ../../SQLiteNET.Opfs.Demo
dotnet clean
dotnet build
```

## Development Tips

### Enable Verbose Logging

**appsettings.Development.json**:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.EntityFrameworkCore": "Information",
      "SQLiteNET.Opfs": "Debug"
    }
  }
}
```

### Monitor Browser Console

Key logs to watch:
- `[OpfsStorageService]` - Service operations
- `[VFS Tracking]` - Dirty page detection
- `[OPFS Interop]` - JSImport data transfers
- `[OPFS Worker]` - Worker operations

### Measure Performance

```csharp
private async Task AddTodoWithTiming()
{
    var sw = Stopwatch.StartNew();

    var todo = new TodoItem { Title = "Test", IsComplete = false };
    DbContext.Todos.Add(todo);
    await DbContext.SaveChangesAsync();

    sw.Stop();
    Console.WriteLine($"⏱ Add + Persist: {sw.ElapsedMilliseconds}ms");
}
```

### Test Fallback Behavior

Temporarily disable incremental sync:

```csharp
protected override async Task OnInitializedAsync()
{
    await OpfsStorage.InitializeAsync();

    // Force full sync for testing
    OpfsStorage.ForceFullSync = true;

    Console.WriteLine("Testing with FULL SYNC mode");
}
```

## Browser DevTools Tips

### View OPFS Contents

Chrome DevTools:
1. Open DevTools (F12)
2. Application tab
3. Storage → Origin Private File System
4. Expand origin → See `TodoDb.db`

### Monitor Storage Quota

```javascript
// Run in console
const estimate = await navigator.storage.estimate();
console.log(`Used: ${estimate.usage / 1024 / 1024} MB`);
console.log(`Quota: ${estimate.quota / 1024 / 1024} MB`);
```

### Clear OPFS Data

```javascript
// Run in console to reset
const root = await navigator.storage.getDirectory();
await root.removeEntry('TodoDb.db');
```

## Documentation

- [../SQLiteNET.Opfs/README.md](../SQLiteNET.Opfs/README.md) - Library documentation
- [INCREMENTAL-SYNC.md](INCREMENTAL-SYNC.md) - Detailed VFS tracking guide
- [JSIMPORT-ANALYSIS.md](JSIMPORT-ANALYSIS.md) - JSImport performance analysis
- [JSIMPORT-WORKER-FIX.md](JSIMPORT-WORKER-FIX.md) - Worker architecture details

## Achieved Status

### ✅ Completed Features

| Feature | Status | Notes |
|---------|--------|-------|
| VFS Tracking | ✅ | Custom SQLite build with dirty page tracking |
| Incremental Sync | ✅ | Only dirty pages written to OPFS |
| JSImport Optimization | ✅ | Zero-copy data transfers |
| MSBuild TypeScript | ✅ | Automatic compilation during build |
| Worker Architecture | ✅ | Single worker instance, global message passing |
| Graceful Fallback | ✅ | Auto-fallback to full sync if VFS unavailable |
| EF Core Integration | ✅ | Transparent OPFS persistence |
| Bulk Operations | ✅ | Pause/resume API for batching |
| Performance Metrics | ✅ | Console logging with timings |

### 📊 Performance Achievements

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Single update | ~200-300ms | ~15-30ms | ~10x faster |
| Data transfer | JSON serialize | Zero-copy | ~100ms saved |
| Bundle size | 23kb (bundled) | 9.3kb (interop) | 60% smaller |
| Memory overhead | N/A | 0.003% | Negligible |

### 🔧 Known Limitations

1. **Browser Support** - Safari OPFS support incomplete
2. **Storage Quota** - Subject to browser limits (~60% of disk)
3. **Local Only** - No built-in cloud sync
4. **Single Origin** - Cannot share data between domains

### 🎯 Future Enhancements (Not Implemented)

- [ ] Cloud sync integration
- [ ] Multi-database support
- [ ] Compression for OPFS storage
- [ ] Background sync (Service Worker)
- [ ] Conflict resolution for offline sync

## License

MIT License - see [LICENSE](../LICENSE) for details.
