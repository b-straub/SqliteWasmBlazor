# SQLiteNET.Opfs - Implementation Details

## Final Architecture

### Using Official SQLite WASM NPM Package

We're now using the **official `@sqlite.org/sqlite-wasm`** npm package (version 3.50.4-build1) instead of manually managing WASM files.

## Build Process

### TypeScript + esbuild

**Location:** `/SQLiteNET.Opfs/Typescript/`

**Source Files:**
1. `sqlite-worker.ts` - Web Worker (runs SQLite with OPFS)
2. `opfs-initializer.ts` - Main thread wrapper (worker communication)

**Worker File (sqlite-worker.ts):**
```typescript
import sqlite3InitModule from '@sqlite.org/sqlite-wasm';

// IMPORTANT: OPFS SAHPool ONLY works in Web Worker context
async function initializeSqlite() {
    sqlite3 = await sqlite3InitModule({
        print: console.log,
        printErr: console.error,
        locateFile: (file: string) => {
            if (file.endsWith('.wasm')) {
                return `/_content/SQLiteNET.Opfs/${file}`;
            }
            return file;
        }
    });

    poolUtil = await sqlite3.installOpfsSAHPoolVfs({
        initialCapacity: 6,
        directory: '/databases',
        name: 'opfs-sahpool',
        clearOnInit: false
    });
}

// Handle messages from main thread
self.onmessage = async (event) => {
    const { id, type, args } = event.data;
    // Process: initialize, open, exec, getFileList, etc.
};
```

**Main Thread File (opfs-initializer.ts):**
```typescript
let worker: Worker | null = null;

export async function initialize(): Promise<InitializeResult> {
    // Create Web Worker
    worker = new Worker(
        '/_content/SQLiteNET.Opfs/sqlite-worker.js',
        { type: 'module' }
    );

    // Send messages to worker
    const result = await sendMessage('initialize');
    return result;
}
```

**Build Command:**
```bash
cd SQLiteNET.Opfs/Typescript
npm install
npm run build
```

**Output:**
- `SQLiteNET.Opfs/wwwroot/sqlite-worker.js` (1.4 MB - includes SQLite WASM library)
- `SQLiteNET.Opfs/Components/OpfsInitializer.razor.js` (11 KB - main thread wrapper)

### Why This Approach?

1. **No Manual File Management** - npm handles SQLite WASM updates
2. **esbuild Bundling** - Single 1.4MB file includes everything
3. **Follows WebAppBase Pattern** - `.razor.js` files alongside components
4. **Development Mode** - Blazor automatically serves `.razor.js` static assets
5. **Production Mode** - Published automatically with `_content/`

## File Structure

```
SQLiteNET.Opfs/
├── Typescript/
│   ├── package.json              # npm dependencies & build scripts
│   ├── tsconfig.json             # TypeScript configuration
│   ├── opfs-initializer.ts       # Source TypeScript
│   └── node_modules/
│       └── @sqlite.org/sqlite-wasm/  # Official SQLite WASM package
│
├── Components/
│   ├── OpfsInitializer.razor     # Blazor component (optional UI)
│   └── OpfsInitializer.razor.js  # ⚡ Generated bundle (1.4MB)
│
├── Services/
│   └── OpfsStorageService.cs     # C# service using JSInterop
│
├── Extensions/
│   └── OpfsDbContextExtensions.cs
│
└── Abstractions/
    └── IOpfsStorage.cs
```

## How It Works

### 1. Development Mode (`dotnet run`)

```
User Request
    ↓
Blazor Dev Server serves .razor.js as static asset
    ↓
JavaScript loads via import("./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js")
    ↓
SQLite WASM initializes with OPFS
    ↓
C# calls JavaScript via JSInterop
    ↓
Database operations
```

### 2. Build Process

```
TypeScript Source (opfs-initializer.ts)
    ↓
esbuild --bundle --format=esm
    ↓
Includes @sqlite.org/sqlite-wasm
    ↓
Outputs OpfsInitializer.razor.js
    ↓
Blazor build includes as static web asset
    ↓
Available at _content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js
```

## C# Service Integration

**OpfsStorageService.cs:**
```csharp
public async Task<bool> InitializeAsync()
{
    // Import the bundled module
    _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
        "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

    // Call exported function
    var result = await _module.InvokeAsync<InitializeResult>("initialize");

    return result.Success;
}
```

## Dependencies

### npm (Development)
```json
{
  "dependencies": {
    "@sqlite.org/sqlite-wasm": "3.50.4-build1"
  },
  "devDependencies": {
    "esbuild": "^0.24.0",
    "typescript": "^5.8.3"
  }
}
```

### NuGet (Runtime)
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0-rc.2.25502.107" />
<PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.0-rc.2.25502.107" />
```

## Build Commands

### Build TypeScript (Manual)
```bash
cd SQLiteNET.Opfs/Typescript
npm run build
```

### Build .NET Solution
```bash
dotnet build SQLiteNET.Opfs.sln
```

The TypeScript build is **separate** from the .NET build. You need to run `npm run build` after modifying TypeScript files.

## Why 1.4MB Bundle?

The bundled file includes:
- SQLite WASM binary (~836 KB)
- SQLite JavaScript wrapper (~400 KB)
- OPFS async proxy (~20 KB)
- TypeScript initialization code
- Sourcemaps (in debug mode)

This is **normal and expected** for SQLite WASM. The file is:
- ✅ Cached by browser
- ✅ Compressed by Blazor (gzip/brotli)
- ✅ Only loaded once per session

## Advantages Over Manual Approach

| Aspect | Manual Files | npm + esbuild |
|--------|-------------|---------------|
| Updates | Manual download | `npm update` |
| Bundling | Multiple files | Single file |
| Dependencies | Manual tracking | package.json |
| Path Resolution | Complex | Simple |
| Tree Shaking | No | Yes (esbuild) |
| Source Maps | Manual | Automatic |

## Demo Application

The demo uses MudBlazor 8.13.0 and demonstrates:
- ✅ Full CRUD operations
- ✅ Persistent storage (survives page refresh)
- ✅ Toast notifications
- ✅ Responsive Material Design UI
- ✅ Enter key support for quick entry

## Browser Requirements

- Chrome 108+
- Firefox 111+
- Safari 16.4+
- Edge 108+

**Note:** OPFS requires modern browsers. Older browsers will fall back to in-memory databases.

## Troubleshooting

### Module not found error
**Solution:** Run `npm run build` in `SQLiteNET.Opfs/Typescript/`

### OPFS not available error
**Check:**
1. Browser version (must support OPFS)
2. Secure context (HTTPS or localhost)
3. Browser compatibility mode disabled

### Large bundle warning (1.4MB)
**This is normal!** SQLite WASM is a complete database engine. The file is:
- Automatically compressed
- Cached by browser
- Only loaded once

## Production Optimization

### Minified Build
```bash
npm run build:release
```

This produces a minified bundle without sourcemaps (~900 KB before compression).

### AOT Compilation
In production, enable AOT compilation for faster startup:
```xml
<PropertyGroup>
  <RunAOTCompilation>true</RunAOTCompilation>
</PropertyGroup>
```

## Future Improvements

1. **Worker-Based Initialization** - Move SQLite to Web Worker for better performance
2. **Lazy Loading** - Only load SQLite when database is first accessed
3. **Multiple Database Support** - Handle multiple DbContexts
4. **Import/Export UI** - Built-in backup/restore components
5. **NuGet Package** - Publish for easy distribution

## Summary

✅ **Clean Architecture** - Follows WebAppBase patterns
✅ **Official Package** - Uses `@sqlite.org/sqlite-wasm`
✅ **Modern Tooling** - TypeScript + esbuild
✅ **Development Ready** - Works in `dotnet run`
✅ **Production Ready** - Automatic publishing
✅ **Maintainable** - npm manages dependencies

The solution is **ready to use** in your WebAppBase project!
