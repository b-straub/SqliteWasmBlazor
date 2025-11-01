# OPFS SAHPool Architecture

## Overview

This project uses **OPFS SAHPool VFS** (SharedAccessHandle Pool Virtual File System) for persistent client-side storage with SQLite WASM in Blazor WebAssembly applications.

## Why OPFS SAHPool?

### Standard OPFS vs OPFS SAHPool

| Feature | Standard OPFS | OPFS SAHPool |
|---------|--------------|--------------|
| **COOP/COEP Headers** | ✅ Required | ❌ Not Required |
| **SharedArrayBuffer** | ✅ Required | ❌ Not Required |
| **Multi-Connection** | ✅ Supported | ❌ Single Connection Only |
| **Performance** | Good | Excellent |
| **Server Configuration** | Complex | Simple |
| **Browser Support** | Modern browsers | Modern browsers |

### Key Advantage: No COOP/COEP Headers

Standard OPFS requires these HTTP response headers:
```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

**OPFS SAHPool does NOT require these headers**, making it ideal for:
- Blazor WebAssembly apps deployed to static hosting (GitHub Pages, Azure Static Web Apps, etc.)
- Applications where server configuration cannot be modified
- Development environments without header configuration

**Source:** [SQLite WASM Official Documentation - Persistence](https://sqlite.org/wasm/doc/trunk/persistence.md#sahpool)

> "Does not require COOP/COEP HTTP headers (and associated restrictions)."

## Architecture

### Web Worker Requirement

**CRITICAL:** OPFS SAHPool **MUST** run in a Web Worker context - it cannot run in the main browser thread.

**Source:** [SQLite WASM Official Documentation](https://sqlite.org/wasm/doc/trunk/persistence.md)

> "Installation will fail if: The proper OPFS APIs are not detected. Note that they are only available in Worker threads, not the main UI thread."

### Implementation

```
┌─────────────────────────────────────────────────────────────┐
│ Main Thread (Blazor UI)                                      │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ OpfsInitializer.razor.js (11 KB)                     │   │
│  │  - Creates Web Worker                                │   │
│  │  - Sends messages to worker                          │   │
│  │  - Receives responses from worker                    │   │
│  └──────────────────────────────────────────────────────┘   │
│                          ↕ postMessage                       │
└─────────────────────────────────────────────────────────────┘
                           ↕
┌─────────────────────────────────────────────────────────────┐
│ Web Worker (sqlite-worker.js - 1.4 MB)                      │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ SQLite WASM + OPFS SAHPool                           │   │
│  │  - Initializes SQLite with SAHPool VFS               │   │
│  │  - Executes SQL commands                             │   │
│  │  - Manages database files in OPFS                    │   │
│  └──────────────────────────────────────────────────────┘   │
│                          ↕                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ OPFS (Origin Private File System)                    │   │
│  │  /databases/TodoDbContext.db                         │   │
│  │  - Persistent storage                                │   │
│  │  - Survives page refresh                             │   │
│  │  - Browser-managed                                   │   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

## Build Process

### Two Separate Bundles

**Why two bundles?**

1. **Worker Bundle (sqlite-worker.js - 1.4 MB)**
   - Runs in Web Worker context (required for OPFS SAHPool)
   - Includes entire SQLite WASM JavaScript library
   - Handles all database operations
   - Built with: `esbuild sqlite-worker.ts --bundle`

2. **Main Thread Bundle (OpfsInitializer.razor.js - 11 KB)**
   - Runs in main browser thread (Blazor UI)
   - Creates and communicates with Web Worker
   - Lightweight wrapper for message passing
   - Built with: `esbuild opfs-initializer.ts --bundle`

3. **WASM Binary (sqlite3.wasm - 836 KB)**
   - Cannot be bundled by esbuild (binary file)
   - Copied separately to wwwroot
   - Loaded by worker via `locateFile` configuration

### Build Commands

```bash
cd SQLiteNET.Opfs/Typescript

# Install dependencies
npm install

# Build all bundles
npm run build

# This runs:
# 1. npm run copy-wasm    → Copies sqlite3.wasm to ../wwwroot/
# 2. npm run build:worker → Bundles sqlite-worker.ts → ../wwwroot/sqlite-worker.js
# 3. npm run build:main   → Bundles opfs-initializer.ts → ../Components/OpfsInitializer.razor.js
```

## Warning Suppression

### The Misleading Warning

When SQLite WASM initializes, it auto-detects available VFS systems and tries to install them. It attempts to install **standard OPFS** first (which requires COOP/COEP headers), fails, and logs a warning:

```
Ignoring inability to install OPFS sqlite3_vfs: Cannot install OPFS:
Missing SharedArrayBuffer and/or Atomics. The server must emit the
COOP/COEP response headers to enable those.
```

**This warning is misleading and harmless** because:
1. We're not using standard OPFS - we're using OPFS SAHPool
2. SAHPool does NOT require COOP/COEP headers
3. SAHPool installs successfully after this warning

### Solution: Filter the Warning

In `sqlite-worker.ts`, we override the `printErr` function to suppress this specific warning:

```typescript
sqlite3 = await sqlite3InitModule({
    print: console.log,
    printErr: (msg: string) => {
        // Filter out misleading OPFS warning - we use SAHPool which doesn't need COOP/COEP
        if (msg.includes('Cannot install OPFS') ||
            msg.includes('Missing SharedArrayBuffer') ||
            msg.includes('COOP/COEP')) {
            // Suppress warning about standard OPFS - we use SAHPool instead
            return;
        }
        console.error(msg);
    },
    locateFile: (file: string) => {
        if (file.endsWith('.wasm')) {
            return `/_content/SQLiteNET.Opfs/${file}`;
        }
        return file;
    }
});

// Install OPFS SAHPool VFS (works WITHOUT COOP/COEP)
poolUtil = await sqlite3.installOpfsSAHPoolVfs({
    initialCapacity: 6,
    directory: '/databases',
    name: 'opfs-sahpool',
    clearOnInit: false
});
```

## Browser Support

OPFS SAHPool requires modern browsers with OPFS support:

- **Chrome/Edge:** 108+
- **Firefox:** 111+
- **Safari:** 16.4+

Check support in browser console:
```javascript
const root = await navigator.storage.getDirectory();
const databases = await root.getDirectoryHandle('databases');
for await (const entry of databases.values()) {
    console.log(entry.name);
}
```

## EF Core Integration

### Drop-in Replacement for InMemoryDatabase

**Before (WebAppBase):**
```csharp
builder.Services.AddWebAppBaseInMemoryDatabase<TodoDbContext>(apiService);
```

**After (SQLiteNET.Opfs):**
```csharp
using SQLiteNET.Opfs.Extensions;

builder.Services.AddOpfsSqliteDbContext<TodoDbContext>();

var host = builder.Build();

// Initialize OPFS in Web Worker
await host.Services.InitializeOpfsAsync();

using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
    await dbContext.ConfigureSqliteForWasmAsync();
    await dbContext.Database.EnsureCreatedAsync();
}

await host.RunAsync();
```

### What Stays the Same

- ✅ All entity models
- ✅ All DbContext operations (Add, Update, Remove)
- ✅ All LINQ queries
- ✅ SaveChanges/SaveChangesAsync
- ✅ Migrations (optional)
- ✅ Sync logic (PendingChange table)

### What Changes

- ➕ OPFS initialization (runs in Web Worker)
- ➕ SQLite configuration for WASM
- ❌ InMemoryDatabaseRoot (no longer needed)

## Performance Characteristics

### Storage Limits

OPFS storage is subject to browser quota management:

- **Chrome/Edge:** Typically 60% of available disk space
- **Firefox:** Up to 50% of available disk space (with prompts)
- **Safari:** Up to 1 GB (with prompts for more)

Check quota usage:
```javascript
const estimate = await navigator.storage.estimate();
console.log(`Used: ${estimate.usage} bytes`);
console.log(`Quota: ${estimate.quota} bytes`);
console.log(`Percentage: ${(estimate.usage / estimate.quota * 100).toFixed(2)}%`);
```

### Single Connection Limitation

OPFS SAHPool supports **only ONE database connection at a time**. This is perfect for:
- ✅ Blazor WebAssembly apps (single-threaded)
- ✅ EF Core usage (single DbContext instance pool)
- ✅ Progressive Web Apps (PWA)

Not suitable for:
- ❌ Multi-worker database access
- ❌ Concurrent write operations from multiple contexts

## Deployment

### Static Hosting

OPFS SAHPool works on **any static hosting** without server configuration:

- ✅ GitHub Pages
- ✅ Azure Static Web Apps
- ✅ Netlify
- ✅ Vercel
- ✅ Cloudflare Pages
- ✅ AWS S3 + CloudFront
- ✅ Firebase Hosting

**No COOP/COEP headers required!**

### Production Build

```bash
cd SQLiteNET.Opfs/Typescript
npm run build:release  # Minified bundles

cd /Users/berni/Projects/SQLiteNET
dotnet publish -c Release
```

**Production bundle sizes:**
- sqlite-worker.js: 900 KB (minified)
- sqlite3.wasm: 836 KB
- OpfsInitializer.razor.js: 8 KB (minified)

**Compressed (Brotli):**
- sqlite-worker.js: ~250 KB
- sqlite3.wasm: ~220 KB
- OpfsInitializer.razor.js: ~3 KB
- **Total: ~473 KB transferred**

## Troubleshooting

### OPFS Not Available

**Error:** "OPFS not available. Browser may not support OPFS or incorrect initialization context."

**Solutions:**
1. Check browser version (Chrome 108+, Firefox 111+, Safari 16.4+)
2. Ensure HTTPS or localhost (OPFS requires secure context)
3. Verify worker initialization completed

### Database Not Persisting

**Check:**
1. OPFS initialization succeeded
2. Database path includes `/databases/` directory
3. Browser didn't clear storage (incognito mode, privacy settings)

### Worker Loading Failed

**Error:** 404 on `/_content/SQLiteNET.Opfs/sqlite-worker.js`

**Solutions:**
1. Run `npm run build` in `SQLiteNET.Opfs/Typescript/`
2. Rebuild .NET solution: `dotnet build`
3. Verify file exists in `SQLiteNET.Opfs/wwwroot/sqlite-worker.js`

## References

- [SQLite WASM Official Documentation](https://sqlite.org/wasm/doc/trunk/index.md)
- [OPFS Persistence Documentation](https://sqlite.org/wasm/doc/trunk/persistence.md)
- [OPFS SAHPool VFS](https://sqlite.org/wasm/doc/trunk/persistence.md#sahpool)
- [MDN: Origin Private File System](https://developer.mozilla.org/en-US/docs/Web/API/File_System_Access_API#origin_private_file_system)

## Summary

✅ **OPFS SAHPool works WITHOUT COOP/COEP headers**
✅ **Runs in Web Worker (required for OPFS access)**
✅ **Perfect for static hosting and Blazor WASM**
✅ **Drop-in replacement for InMemoryDatabase**
✅ **True persistent storage with excellent performance**

The architecture is production-ready and follows official SQLite WASM best practices!
