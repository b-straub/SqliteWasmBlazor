# TypeScript Build

This directory contains the TypeScript source files that get bundled into the component code-behind.

## Source Files

### opfs-sahpool.ts
The OPFS VFS implementation (our code, converted to TypeScript):
- Pool-based file management with Synchronous Access Handles
- 4096-byte header system with metadata
- Based on SQLite team's OpfsSAHPool (Roy Hashimoto's AccessHandlePoolVFS)

### opfs-native-vfs.ts
Initialization wrapper and management functions:
- Imports and initializes opfs-sahpool
- Exposes functions for C# interop (initialize, getFileList, exportDatabase, etc.)
- This is the main entry point

## Build Process

```bash
npm run build
```

**What it does:**
1. Bundles `opfs-sahpool.ts` + `opfs-native-vfs.ts` with esbuild
2. Outputs to `../Components/OpfsInitializer.razor.js`
3. Creates source maps for debugging

## How It Works

```
opfs-native-vfs.ts (entry point)
  ↓ imports
opfs-sahpool.ts (VFS implementation)
  ↓ bundled by esbuild
OpfsInitializer.razor.js (56.6kb)
  ↓ loaded by Blazor
C# OpfsStorageService calls initialize()
  ↓
globalThis.opfsSAHPool is ready
  ↓
JsVfsInterop.sqlite3_jsvfs_register() connects C to JS
```

## Relationship to native-build/

The `native-build/opfs-sahpool.js` is the **same code** but kept there for:
1. Reference: Shows what the C layer (jsvfs.c) expects
2. Documentation: EM_JS hooks in jsvfs.c call this API
3. Bundling: Eventually bundled by esbuild from this TypeScript version

**Single source of truth**: This TypeScript version
**Reference copy**: native-build/opfs-sahpool.js (for C developers)

## Output

- `../Components/OpfsInitializer.razor.js` - Bundled JavaScript (ESM)
- Includes inline source maps for debugging
- Automatically included by Blazor as component code-behind
