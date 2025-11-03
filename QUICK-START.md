# SQLiteNET.Opfs - Quick Start Guide

Get started with high-performance SQLite persistence in Blazor WebAssembly in under 10 minutes.

## Prerequisites

- .NET 10.0 SDK (or .NET 8.0+)
- Emscripten SDK 3.1.56
- Node.js 18+ and npm

## Step 1: Build Custom SQLite Library

### Install Emscripten

```bash
# Clone Emscripten SDK
git clone https://github.com/emscripten-core/emsdk.git
cd emsdk

# Install and activate version 3.1.56 (matches .NET 10 WASM runtime)
./emsdk install 3.1.56
./emsdk activate 3.1.56
source ./emsdk_env.sh  # On Windows: emsdk_env.bat
```

### Build SQLite with VFS Tracking

```bash
cd /path/to/SQLiteNET.Opfs/Native
./build_sqlite.sh
```

**Output**: `lib/e_sqlite3.a` (custom SQLite library)

## Step 2: Configure Your Blazor App

### 2.1 Update Project File

**YourApp.csproj**:
```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <WasmBuildNative>true</WasmBuildNative>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0-rc.2.25502.107" />

    <!-- Exclude packaged SQLite - using custom build -->
    <PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" Version="2.1.11"
                      ExcludeAssets="native;buildTransitive" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SQLiteNET.Opfs\SQLiteNET.Opfs.csproj" />
  </ItemGroup>

  <!-- Use custom SQLite library with VFS tracking -->
  <ItemGroup>
    <NativeFileReference Include="..\SQLiteNET.Opfs\Native\lib\e_sqlite3.a" />
  </ItemGroup>
</Project>
```

### 2.2 Register Service

**Program.cs**:
```csharp
using SQLiteNET.Opfs.Abstractions;
using SQLiteNET.Opfs.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Register OPFS storage
builder.Services.AddScoped<IOpfsStorage, OpfsStorageService>();

// Register your DbContext
builder.Services.AddDbContext<YourDbContext>();

await builder.Build().RunAsync();
```

## Step 3: Create DbContext

**YourDbContext.cs**:
```csharp
using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.Abstractions;

public class YourDbContext : DbContext
{
    private readonly IOpfsStorage _opfsStorage;

    public YourDbContext(IOpfsStorage opfsStorage)
    {
        _opfsStorage = opfsStorage;
    }

    public DbSet<YourModel> YourModels => Set<YourModel>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=app.db");
    }

    // Automatically persist to OPFS after every save
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        int result = await base.SaveChangesAsync(cancellationToken);

        // Persist to OPFS (automatically uses incremental sync if available)
        await _opfsStorage.Persist("app.db");

        return result;
    }
}
```

## Step 4: Initialize OPFS

**In your main page component**:
```csharp
@page "/"
@inject IOpfsStorage OpfsStorage
@inject YourDbContext DbContext

@code {
    protected override async Task OnInitializedAsync()
    {
        // Initialize OPFS worker
        bool initialized = await OpfsStorage.InitializeAsync();

        if (!initialized)
        {
            Console.WriteLine("❌ OPFS initialization failed");
            return;
        }

        // Check if incremental sync is available
        if (OpfsStorage.IsIncrementalSyncEnabled)
        {
            Console.WriteLine("✓ Incremental sync enabled (10x faster!)");
        }

        // Load database from OPFS to MEMFS (if exists)
        await OpfsStorage.Load("app.db");

        // Apply EF Core migrations
        await DbContext.Database.MigrateAsync();
    }
}
```

## Step 5: Use Entity Framework Normally

```csharp
// Add
var item = new YourModel { Name = "Test" };
DbContext.YourModels.Add(item);
await DbContext.SaveChangesAsync();  // Automatically persists to OPFS

// Query
var items = await DbContext.YourModels.ToListAsync();

// Update
item.Name = "Updated";
await DbContext.SaveChangesAsync();  // Only dirty pages written (fast!)

// Delete
DbContext.YourModels.Remove(item);
await DbContext.SaveChangesAsync();
```

## Step 6: Build and Run

```bash
dotnet build
dotnet run
```

Navigate to `https://localhost:5001` and check the browser console:

**Expected output**:
```
[OpfsStorageService] ✓ OPFS initialized: OPFS Worker initialized successfully
[OpfsStorageService] ✓ JSImport interop initialized
[OpfsStorageService] ✓ VFS tracking initialized (page size: 4096 bytes)
```

## Verification

### 1. Check Incremental Sync

After making a database change, you should see:
```
[VFS Tracking] Found 2 dirty pages for 'app.db'
[OpfsStorageService] Persisting (incremental): app.db - 2 dirty pages
[OpfsStorageService] ✓ JSImport: Written 2 pages (8 KB)
```

### 2. Test Persistence

1. Add some data
2. Refresh the page (F5)
3. Data should still be there

### 3. View in DevTools

Chrome DevTools → Application → Storage → Origin Private File System → Your origin → `app.db`

## Advanced: Bulk Operations

For bulk inserts/updates, use pause/resume:

```csharp
public async Task BulkImport(List<YourModel> items)
{
    // Pause automatic persistence
    OpfsStorage.PauseAutomaticPersistent();

    try
    {
        DbContext.YourModels.AddRange(items);
        await DbContext.SaveChangesAsync();  // No OPFS write yet
    }
    finally
    {
        // Resume and persist all changes at once
        await OpfsStorage.ResumeAutomaticPersistent();
    }
}
```

## Troubleshooting

### ⚠ VFS tracking not available

**Console**: `[OpfsStorageService] ⚠ VFS tracking unavailable`

**Solution**:
```bash
# Rebuild custom SQLite library
cd SQLiteNET.Opfs/Native
./build_sqlite.sh

# Clean and rebuild
cd ../../YourApp
dotnet clean
dotnet build
```

### ⚠ TypeScript errors

**Solution**:
```bash
cd SQLiteNET.Opfs/Typescript
npm install
npm run build
```

### ⚠ Slow performance

**Check**:
```csharp
Console.WriteLine($"Incremental sync: {OpfsStorage.IsIncrementalSyncEnabled}");
Console.WriteLine($"Force full sync: {OpfsStorage.ForceFullSync}");
```

If incremental sync is disabled, VFS tracking is not available.

## What You Get

✅ **10x Faster Updates** - Only dirty pages written to OPFS
✅ **Zero-Copy Transfers** - JSImport eliminates JSON serialization
✅ **Automatic Persistence** - Data survives page reloads
✅ **EF Core Compatible** - Works with existing code
✅ **Graceful Fallback** - Falls back to full sync if needed

## Performance Example

**Single Todo Update**:
- Without incremental sync: ~200-300ms
- With incremental sync: ~20-30ms
- **Result**: 10x faster ⚡

**Bulk Insert (50,000 records)**:
- With pause/resume: ~3-5 seconds
- Per-record persist: Hours (impractical)
- **Result**: Practical bulk operations ⚡

## Next Steps

- Read [SQLiteNET.Opfs/README.md](SQLiteNET.Opfs/README.md) for full API documentation
- See [SQLiteNET.Opfs.Demo/README.md](SQLiteNET.Opfs.Demo/README.md) for demo app
- Check [INCREMENTAL-SYNC.md](SQLiteNET.Opfs.Demo/INCREMENTAL-SYNC.md) for technical details
- Review [PROJECT-STATUS.md](SQLiteNET.Opfs.Demo/PROJECT-STATUS.md) for current status

## Support

- **GitHub Issues**: Report bugs or ask questions
- **Documentation**: See README files in each project
- **Demo App**: Run `SQLiteNET.Opfs.Demo` for examples

---

**Total Setup Time**: ~10 minutes
**Performance Gain**: 10x faster
**Code Changes**: Minimal (just DbContext)
