# Experiment: Native VFS Bridge for e_sqlite3.a

## Goal

Eliminate the dual-instance architecture by building a custom e_sqlite3.a with JavaScript VFS support, allowing EF Core to directly use OPFS without needing a separate sqlite3.wasm instance.

## Current Architecture (Baseline)

```
e_sqlite3.a (EF Core) → MEMFS
         ↓ (file copy)
sqlite3.wasm (worker) → OPFS

Bundle size: ~1.4 MB sqlite3.wasm (mostly unused)
```

## Target Architecture

```
e_sqlite3.a (EF Core) → JS VFS Bridge → OPFS Worker

Eliminate: sqlite3.wasm bundle
Gain: Direct VFS integration
```

## Investigation Plan

### Phase 1: Understand SQLite VFS Interface

**Tasks:**
1. Study `sqlite3_vfs` struct definition
2. Understand VFS methods: xOpen, xRead, xWrite, xLock, xUnlock, xClose
3. Review how @sqlite.org/sqlite-wasm implements OPFS VFS
4. Document the VFS callback signatures and data flow

**Resources:**
- SQLite VFS documentation: https://www.sqlite.org/vfs.html
- @sqlite.org/sqlite-wasm source: `node_modules/@sqlite.org/sqlite-wasm/sqlite-wasm/jswasm/`
- Emscripten EM_JS documentation: https://emscripten.org/docs/porting/connecting_cpp_and_javascript/Interacting-with-code.html

### Phase 2: Build Custom e_sqlite3.a with JS Hooks

**Tasks:**
1. Clone SQLite source code
2. Add Emscripten EM_JS/EM_ASM hooks for VFS callbacks
3. Create `sqlite3_vfs` implementation that calls out to JavaScript
4. Compile with Emscripten to produce `e_sqlite3_jsvfs.a`
5. Test that it can call JavaScript VFS methods

**Key Files to Create:**
```
sqlite_custom_build/
├── sqlite3.c                    # Official SQLite amalgamation
├── sqlite3_jsvfs.c              # Custom VFS with JS hooks
├── build_wasm.sh                # Emscripten build script
└── test_vfs.html                # Simple test harness
```

**Example VFS Hook (conceptual):**
```c
// sqlite3_jsvfs.c
#include <emscripten.h>

EM_JS(int, js_vfs_open, (const char* zName, int flags), {
  return Module.opfsVfs.xOpen(UTF8ToString(zName), flags);
});

EM_JS(int, js_vfs_read, (void* pFile, void* zBuf, int iAmt, sqlite3_int64 iOfst), {
  const buffer = new Uint8Array(HEAPU8.buffer, zBuf, iAmt);
  return Module.opfsVfs.xRead(pFile, buffer, iAmt, Number(iOfst));
});

// Implement all VFS methods...
static int jsvfsRead(sqlite3_file *pFile, void *zBuf, int iAmt, sqlite3_int64 iOfst){
  return js_vfs_read(pFile, zBuf, iAmt, iOfst);
}

// Register VFS
static sqlite3_vfs jsvfs = {
  .iVersion = 1,
  .xOpen = jsvfsOpen,
  .xRead = jsvfsRead,
  // ... all methods
};

void sqlite3_register_jsvfs() {
  sqlite3_vfs_register(&jsvfs, 1);
}
```

### Phase 3: Create JavaScript OPFS VFS Implementation

**Tasks:**
1. Extract SAHPool VFS logic from @sqlite.org/sqlite-wasm (or reimplement)
2. Create standalone JavaScript VFS that matches the callback interface
3. Register it on `Module.opfsVfs` before SQLite initialization
4. Test file operations work correctly

**Key Files to Create:**
```
SQLiteNET.Opfs/Typescript/
├── native-vfs-bridge.ts         # Main coordinator
├── opfs-vfs-impl.ts              # OPFS VFS implementation
└── vfs-worker.ts                 # Worker for OPFS operations
```

**Example VFS Implementation (conceptual):**
```typescript
// opfs-vfs-impl.ts
export class OpfsVfs {
  private handles = new Map<number, FileSystemFileHandle>();

  async xOpen(filename: string, flags: number): Promise<number> {
    const root = await navigator.storage.getDirectory();
    const handle = await root.getFileHandle(filename, { create: true });
    const fileId = this.handles.size + 1;
    this.handles.set(fileId, handle);
    return fileId;
  }

  async xRead(fileId: number, buffer: Uint8Array, amount: number, offset: number): Promise<number> {
    const handle = this.handles.get(fileId);
    const file = await handle.getFile();
    const data = await file.slice(offset, offset + amount).arrayBuffer();
    buffer.set(new Uint8Array(data));
    return data.byteLength;
  }

  // ... implement all VFS methods
}

// Register for native code
(window as any).Blazor.runtime.Module.opfsVfs = new OpfsVfs();
```

### Phase 4: Integrate with SqlitePCLRaw

**Tasks:**
1. Create custom NuGet package: `SqlitePCLRaw.lib.e_sqlite3.browser`
2. Include custom-built e_sqlite3_jsvfs.a
3. Add C# P/Invoke for `sqlite3_register_jsvfs()`
4. Update OpfsServiceCollectionExtensions to initialize JS VFS

**Key Files to Create:**
```
SqlitePCLRaw.lib.e_sqlite3.browser/
├── SqlitePCLRaw.lib.e_sqlite3.browser.csproj
├── build/
│   └── e_sqlite3_jsvfs.a
└── NativeMethods.cs              # P/Invoke declarations
```

**Example C# Integration:**
```csharp
// NativeMethods.cs
public static class SqliteJsVfs
{
    [DllImport("e_sqlite3_jsvfs")]
    public static extern void sqlite3_register_jsvfs();
}

// OpfsServiceCollectionExtensions.cs
public static async Task InitializeOpfsAsync(this IServiceProvider services)
{
    // Initialize JavaScript VFS
    var js = services.GetRequiredService<IJSRuntime>();
    await js.InvokeVoidAsync("initializeOpfsVfs");

    // Register native VFS
    SqliteJsVfs.sqlite3_register_jsvfs();

    // Tell EF Core to use it
    var factory = services.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var context = await factory.CreateDbContextAsync();
    await context.Database.ExecuteSqlRawAsync("PRAGMA vfs='jsvfs'");
}
```

### Phase 5: Testing and Validation

**Tasks:**
1. Create test suite for VFS operations
2. Verify EF Core migrations work
3. Test concurrent access and locking
4. Performance benchmark vs current architecture
5. Test edge cases (large files, transactions, WAL mode)

**Success Criteria:**
- ✅ All EF Core operations work
- ✅ OPFS persistence works
- ✅ No data corruption
- ✅ Performance comparable or better
- ✅ Bundle size reduced (no sqlite3.wasm)

### Phase 6: Documentation and Publishing

**Tasks:**
1. Document build process for custom e_sqlite3.a
2. Create NuGet package
3. Update SQLiteNET.Opfs library to use native VFS
4. Publish to GitHub with examples
5. Write blog post explaining the approach

## Challenges and Risks

### Challenge 1: Emscripten Async/Sync Mismatch

**Problem:** OPFS is async, SQLite VFS expects sync operations

**Solutions:**
- Use Atomics and SharedArrayBuffer for synchronous OPFS (requires COOP/COEP headers)
- Use synchronous Web Worker postMessage (possible with Atomics.wait)
- Restructure VFS to be async (requires patching SQLite core - difficult)

### Challenge 2: Memory Management

**Problem:** Passing buffers between C and JavaScript

**Solutions:**
- Use Emscripten heap views (HEAPU8)
- Careful pointer management
- Memory leak prevention

### Challenge 3: VFS Complexity

**Problem:** VFS has 20+ methods, must implement all correctly

**Solutions:**
- Start with minimal VFS (just xRead/xWrite)
- Gradually add features
- Copy proven logic from @sqlite.org/sqlite-wasm where possible

### Challenge 4: Debugging

**Problem:** Hard to debug native + JS bridge

**Solutions:**
- Extensive logging
- Simple test cases first
- Use browser DevTools + WASM debugging

## Timeline Estimate

- Phase 1 (Research): 1-2 days
- Phase 2 (Custom build): 3-5 days
- Phase 3 (JS VFS): 2-3 days
- Phase 4 (Integration): 2-3 days
- Phase 5 (Testing): 2-4 days
- Phase 6 (Documentation): 1-2 days

**Total: 11-19 days of focused work**

With AI assistance, likely on the lower end.

## Expected Benefits

### If Successful:

1. **Bundle Size Reduction:**
   - Remove: 1.4 MB (sqlite3.wasm)
   - Add: ~50 KB (custom VFS code)
   - **Net savings: ~1.35 MB**

2. **Architectural Simplicity:**
   - Single SQLite instance
   - No file copying overhead
   - Direct VFS integration

3. **Performance:**
   - No MEMFS → OPFS file copying
   - Real-time persistence
   - Better cache coherency

4. **Community Impact:**
   - First proper OPFS VFS for .NET
   - Could be contributed to SqlitePCLRaw
   - Benefits entire Blazor WASM ecosystem

## Resources Needed

- Access to Emscripten toolchain
- Understanding of C (SQLite VFS API)
- Understanding of JavaScript (OPFS APIs)
- Time for iterative debugging

## Next Steps

1. ✅ Commit current working architecture (baseline)
2. ✅ Create experiment branch
3. ✅ Document investigation plan (this file)
4. ⏭️ Phase 1: Study SQLite VFS interface
5. ⏭️ Phase 2: Create minimal VFS with one method (xOpen)
6. ⏭️ Iterate until complete

## Notes

- Keep baseline working on `master` branch
- All experiments on `experiment/native-vfs-bridge` branch
- Can always fall back to current architecture if blocked
- Document learnings even if experiment fails

---

**Branch:** `experiment/native-vfs-bridge`
**Started:** 2025-11-01
**Status:** Planning phase
