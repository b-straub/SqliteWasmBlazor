# JSImport/JSExport Analysis for OPFS Interop

## Overview

This document analyzes whether migrating from `IJSRuntime` to `[JSImport]`/`[JSExport]` would benefit the OPFS storage implementation.

## Current Implementation (IJSRuntime)

**Pattern**: Dynamic JavaScript interop using `IJSRuntime.InvokeAsync<T>()`

**Example from OpfsStorageService.cs**:
```csharp
_module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
    "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

var result = await _module.InvokeAsync<InitializeResult>("initialize");

await _module.InvokeVoidAsync("persist", fileName);
```

### Pros
- ✅ Simple to use
- ✅ Works with any JavaScript module
- ✅ Supports dynamic module loading
- ✅ No additional build configuration

### Cons
- ❌ Runtime overhead (JSON serialization)
- ❌ No compile-time type checking
- ❌ Reflection-based marshalling
- ❌ String-based method names (typo-prone)
- ❌ Performance overhead for large data transfers

## JSImport/JSExport Pattern (.NET 7+)

**Pattern**: Compile-time source-generated JavaScript interop

**Requirements**:
- .NET 7+ (you're on .NET 10 ✅)
- `[JSImport]` for C# → JS calls
- `[JSExport]` for JS → C# callbacks
- Static module registration

### How It Works

1. **C# Side**: Declare JS functions with `[JSImport]` attribute
2. **JS Side**: Export functions as ES6 modules
3. **Build Time**: Source generator creates marshalling code
4. **Runtime**: Direct WASM function calls (no JSON serialization)

## Migration Example

### Current Code (IJSRuntime)

**C# (OpfsStorageService.cs)**:
```csharp
public class OpfsStorageService : IOpfsStorage
{
    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;

    public async Task<bool> InitializeAsync()
    {
        _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

        var result = await _module.InvokeAsync<InitializeResult>("initialize");
        return result.Success;
    }

    public async Task Persist(string fileName)
    {
        await _module.InvokeVoidAsync("persist", fileName);
    }
}
```

**JavaScript (opfs-initializer.ts)**:
```typescript
export async function initialize(): Promise<InitializeResult> {
    // ... implementation
}

export async function persist(filename: string): Promise<void> {
    // ... implementation
}
```

### Proposed Code (JSImport)

**C# (OpfsInterop.cs - New file)**:
```csharp
using System.Runtime.InteropServices.JavaScript;

namespace SQLiteNET.Opfs.Interop;

public partial class OpfsInterop
{
    // Module path for JSImport
    private const string ModuleName = "OpfsModule";

    // Import JS functions
    [JSImport("initialize", ModuleName)]
    public static partial Task<InitializeResult> InitializeAsync();

    [JSImport("persist", ModuleName)]
    public static partial Task PersistAsync(string filename);

    [JSImport("persistDirtyPages", ModuleName)]
    public static partial Task PersistDirtyPagesAsync(
        string filename,
        [JSMarshalAs<JSType.Array<JSType.Object>>] PageData[] pages);

    [JSImport("load", ModuleName)]
    public static partial Task LoadAsync(string filename);

    [JSImport("getFileList", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.String>>]
    public static partial Task<string[]> GetFileListAsync();

    [JSImport("getCapacity", ModuleName)]
    public static partial Task<int> GetCapacityAsync();

    // Module initialization (called once at startup)
    [ModuleInitializer]
    public static void RegisterModule()
    {
        // Register the JavaScript module
        JSHost.ImportAsync(
            ModuleName,
            "/_content/SQLiteNET.Opfs/opfs-interop.js");
    }
}

// Data structures for marshalling
[Serializable]
public struct InitializeResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public int Capacity { get; set; }
    public int FileCount { get; set; }
}

[Serializable]
public struct PageData
{
    public uint PageNumber { get; set; }

    [JSMarshalAs<JSType.MemoryView>]
    public byte[] Data { get; set; }  // Marshalled as ArrayBuffer
}
```

**C# (OpfsStorageService.cs - Updated)**:
```csharp
public class OpfsStorageService : IOpfsStorage
{
    // No IJSRuntime dependency!

    public async Task<bool> InitializeAsync()
    {
        var result = await OpfsInterop.InitializeAsync();
        IsReady = result.Success;
        return result.Success;
    }

    public async Task Persist(string fileName)
    {
        if (IsIncrementalSyncEnabled && !ForceFullSync)
        {
            await PersistIncremental(fileName);
        }
        else
        {
            await OpfsInterop.PersistAsync(fileName);
        }
    }

    private async Task PersistIncremental(string fileName)
    {
        // Get dirty pages from VFS
        int rc = VfsInterop.GetDirtyPages(fileName, out uint pageCount, out IntPtr pagesPtr);

        if (pageCount == 0) return;

        uint[] dirtyPages = VfsInterop.MarshalPages(pagesPtr, pageCount);

        // Read pages from MEMFS
        var pagesToWrite = await ReadDirtyPagesFromMemfs(fileName, dirtyPages);

        // Direct call - no IJSObjectReference
        await OpfsInterop.PersistDirtyPagesAsync(fileName, pagesToWrite.ToArray());
    }
}
```

**JavaScript (opfs-interop.js - New file)**:
```javascript
// Direct exports for JSImport
export async function initialize() {
    // Existing implementation from opfs-initializer.ts
    return {
        success: true,
        message: "Initialized",
        capacity: 10,
        fileCount: 0
    };
}

export async function persist(filename) {
    // Existing implementation
}

export async function persistDirtyPages(filename, pages) {
    // pages is directly a JavaScript array of objects
    // No JSON deserialization needed!
    for (const page of pages) {
        const { pageNumber, data } = page;
        // data is already a Uint8Array (via MemoryView marshalling)
        await writePageToOpfs(filename, pageNumber, data);
    }
}

export async function load(filename) {
    // Existing implementation
}

export async function getFileList() {
    // Existing implementation
}

export async function getCapacity() {
    // Existing implementation
}
```

## Performance Comparison

### Current (IJSRuntime)

**Data Flow for persistDirtyPages**:
```
C# PageData[]
  → JSON.stringify() [SLOW]
  → JavaScript string
  → JSON.parse() [SLOW]
  → JavaScript objects
  → int[] data
  → new Uint8Array(data) [COPY]
```

**Overhead**:
- JSON serialization/deserialization
- Type conversions (int[] → byte[])
- Multiple memory copies
- String allocation for method names

### JSImport

**Data Flow for persistDirtyPages**:
```
C# PageData[]
  → Direct WASM memory sharing [FAST]
  → JavaScript Uint8Array views
  → Zero-copy access
```

**Benefits**:
- No JSON overhead
- Direct memory access via `[JSMarshalAs<JSType.MemoryView>]`
- Compile-time type checking
- Inlined method calls

### Estimated Performance Gains

For 1249 dirty pages (current bulk update scenario):

| Metric | IJSRuntime | JSImport | Improvement |
|--------|-----------|----------|-------------|
| Serialization | ~50-100ms | 0ms | ~100ms saved |
| Memory copies | 3-4 copies | 1 copy | ~30ms saved |
| Type safety | Runtime | Compile-time | N/A |
| Method lookup | Dynamic | Static | ~5ms saved |

**Total estimated savings**: ~135ms per incremental sync operation

For 50,000 entry bulk operations, this could save **significant time**.

## Complexity Comparison

### Migration Effort

| Task | Effort | Notes |
|------|--------|-------|
| Create OpfsInterop.cs | Medium | ~2-3 hours |
| Update OpfsStorageService | Low | ~1 hour |
| Refactor JavaScript exports | Low | ~1 hour |
| Update module loading | Medium | JSHost.ImportAsync setup |
| Testing | High | Full integration testing |
| **Total** | **1-2 days** | **For full migration** |

### Maintenance

| Aspect | IJSRuntime | JSImport |
|--------|-----------|----------|
| Type safety | Runtime errors | Compile-time errors |
| Refactoring | Manual string updates | Automatic |
| Breaking changes | Discovered at runtime | Discovered at build |
| IntelliSense | Limited | Full support |

## Limitations of JSImport

### What It Can't Do (Yet)

1. **Dynamic module loading**: Modules must be registered at startup
2. **IJSObjectReference pattern**: No direct equivalent for module instances
3. **eval() calls**: Current code uses `eval()` for MEMFS access
4. **Complex object graphs**: Limited to flat structures with attributes

### Workarounds

**Current eval() usage**:
```csharp
var result = await _jsRuntime.InvokeAsync<PageReadResultRaw>("eval",
    $@"(() => {{
        const fs = window.Blazor?.runtime?.Module?.FS;
        // ... read from MEMFS
    }})()");
```

**JSImport approach**: Export dedicated functions instead:
```javascript
// New export
export function readPagesFromMemfs(filename, pageNumbers) {
    const fs = window.Blazor?.runtime?.Module?.FS;
    if (!fs) return { success: false, error: 'FS not available' };

    const pages = [];
    const fileData = fs.readFile(`/${filename}`);

    for (const pageNum of pageNumbers) {
        const offset = pageNum * 4096;
        const end = Math.min(offset + 4096, fileData.length);
        pages.push({
            pageNumber: pageNum,
            data: fileData.subarray(offset, end)  // Zero-copy view
        });
    }

    return { success: true, pages };
}
```

```csharp
[JSImport("readPagesFromMemfs", ModuleName)]
public static partial Task<PageReadResult> ReadPagesFromMemfsAsync(
    string filename,
    [JSMarshalAs<JSType.Array<JSType.Number>>] uint[] pageNumbers);
```

## Recommendation

### ✅ Use JSImport For

1. **Hot paths** (called frequently):
   - `persistDirtyPages()` - called on every save with incremental sync
   - `readPagesFromMemfs()` - data-intensive operation
   - `getFileList()` - potentially large arrays

2. **Data-intensive operations**:
   - Page data transfers (currently arrays of int arrays)
   - Bulk operations

### ❌ Keep IJSRuntime For

1. **Module initialization** (`import()` call)
2. **One-time setup** operations
3. **Infrequent calls** (getCapacity, cleanup)

### Hybrid Approach (Recommended)

**Best of both worlds**:

```csharp
public class OpfsStorageService : IOpfsStorage
{
    private readonly IJSRuntime _jsRuntime;  // For initialization

    public async Task<bool> InitializeAsync()
    {
        // Use IJSRuntime for module loading
        _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

        // Then register JSImport module
        await OpfsInterop.RegisterModuleAsync();

        var result = await OpfsInterop.InitializeAsync();
        return result.Success;
    }

    private async Task PersistIncremental(string fileName)
    {
        // Use JSImport for performance-critical data transfer
        var pages = await OpfsInterop.ReadPagesFromMemfsAsync(fileName, dirtyPages);
        await OpfsInterop.PersistDirtyPagesAsync(fileName, pages);
    }
}
```

## Conclusion

### Should You Migrate?

**YES, for performance-critical paths**:

| Factor | Impact | Priority |
|--------|--------|----------|
| Performance | High (~100ms+ savings per operation) | ⭐⭐⭐⭐⭐ |
| Type safety | Medium (prevents runtime errors) | ⭐⭐⭐⭐ |
| Maintainability | High (compile-time checks) | ⭐⭐⭐⭐ |
| Migration effort | Medium (1-2 days) | ⭐⭐⭐ |
| Risk | Low (can be done incrementally) | ⭐⭐⭐⭐⭐ |

**Recommendation**: Implement hybrid approach:
1. Keep IJSRuntime for module loading and initialization
2. Migrate performance-critical calls to JSImport:
   - `persistDirtyPages()`
   - `readPagesFromMemfs()`
3. Measure performance improvement
4. Gradually migrate other calls if beneficial

### Next Steps

If you decide to proceed:

1. Create `Interop/OpfsInterop.cs` with `[JSImport]` declarations
2. Create `Typescript/opfs-interop.ts` with direct exports
3. Update `persistDirtyPages` flow to use JSImport
4. Benchmark before/after for incremental sync
5. Migrate other hot paths if gains are significant

### Estimated ROI

For your current use case (50,000 entries, bulk updates):
- **Time saved per operation**: ~100-150ms
- **Operations per bulk update**: 100-1000
- **Total time saved**: **10-150 seconds** per bulk operation

Given the performance concerns you've raised, **this migration is worth considering**.
