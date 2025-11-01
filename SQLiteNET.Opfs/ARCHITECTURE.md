# SQLiteNET.Opfs Architecture

## Overview

This library provides OPFS (Origin Private File System) persistence for EF Core SQLite databases in Blazor WebAssembly applications.

## Dual-Instance Architecture

This project uses **TWO separate SQLite instances** by design:

### 1. e_sqlite3.a (from NuGet: `SqlitePCLRaw.lib.e_sqlite3`)

**Purpose**: Database operations
- Used by Entity Framework Core for all SQL operations
- Runs in the .NET WASM runtime using Emscripten MEMFS
- Handles queries, migrations, change tracking, LINQ translations
- This is the "main" SQLite instance

### 2. sqlite3.wasm (from npm: `@sqlite.org/sqlite-wasm`)

**Purpose**: File persistence only
- Provides the OPFS SAHPool VFS implementation
- Runs in a Web Worker
- Handles copying database files between MEMFS ↔ OPFS
- **Does NOT execute any SQL queries**
- Only used for import/export file operations

## Why This Architecture?

### Technical Constraints

1. **e_sqlite3.a cannot use JavaScript VFS**
   - It's compiled native C code
   - Expects native VFS implementations (not JavaScript)
   - Uses Emscripten MEMFS for in-memory file operations

2. **OPFS APIs are JavaScript-only**
   - OPFS requires Web Worker context
   - No C/native bindings available
   - Cannot be accessed from .NET WASM directly

3. **SAHPool VFS is tightly coupled**
   - Cannot be extracted from sqlite3.wasm
   - Depends on sqlite3.capi, sqlite3.wasm heap, and Jaccwabyt
   - ~2000+ lines of integrated code

### Design Decision

Rather than:
- ❌ Reimplement complex OPFS VFS from scratch
- ❌ Abandon EF Core (would lose LINQ, migrations, change tracking)
- ❌ Try to bridge JavaScript VFS to native SQLite (not feasible)

We chose to:
- ✅ Use e_sqlite3.a for database operations (proven EF Core integration)
- ✅ Use @sqlite.org/sqlite-wasm for file persistence (proven OPFS implementation)
- ✅ Bridge the two via OpfsStorageService

### Performance Impact

The dual-instance overhead is **acceptable** because:
- Modern browsers handle WASM efficiently
- The worker SQLite instance is only used for file I/O (not SQL execution)
- SAHPool VFS provides transaction safety and performance optimizations
- Alternative (custom OPFS implementation) would require extensive testing

## Data Flow

```
┌─────────────────────────────────────┐
│  Blazor App (Main Thread)           │
│  ┌─────────────────────────────┐   │
│  │  EF Core DbContext          │   │
│  │  Uses e_sqlite3.a           │   │
│  │  Data Source=/TodoDb.db     │   │
│  └──────────┬──────────────────┘   │
│             │                       │
│             ↓                       │
│  ┌─────────────────────────────┐   │
│  │  Emscripten MEMFS           │   │
│  │  /TodoDb.db (in-memory)     │   │
│  └──────────┬──────────────────┘   │
│             │                       │
│             ↓                       │
│  ┌─────────────────────────────┐   │
│  │  OpfsStorageService         │   │
│  │  JS Interop Bridge          │   │
│  └──────────┬──────────────────┘   │
└─────────────┼───────────────────────┘
              │ postMessage
              ↓
┌─────────────────────────────────────┐
│  Web Worker (worker.js)             │
│  ┌─────────────────────────────┐   │
│  │  sqlite3.wasm               │   │
│  │  SAHPool VFS                │   │
│  └──────────┬──────────────────┘   │
│             │                       │
│             ↓                       │
│  ┌─────────────────────────────┐   │
│  │  OPFS (Browser Storage)     │   │
│  │  /databases/TodoDb.db       │   │
│  └─────────────────────────────┘   │
└─────────────────────────────────────┘
```

## Components

### TypeScript Layer

- **opfs-initializer.ts**: Main thread API
  - Manages Web Worker lifecycle
  - Provides `persist()` and `load()` functions
  - Bridges Emscripten MEMFS ↔ OPFS

- **sqlite-worker.ts**: Web Worker implementation
  - Initializes sqlite3.wasm with SAHPool VFS
  - Handles file import/export operations
  - Manages OPFS directory structure

### C# Layer

- **OpfsStorageService**: JavaScript interop wrapper
  - Calls TypeScript functions via IJSRuntime
  - Thread-safe singleton

- **OpfsDbContextInterceptor**: EF Core integration
  - Intercepts database write operations
  - Triggers throttled persistence (50ms debounce)
  - Automatically syncs changes to OPFS

- **OpfsPooledDbContextFactory**: Factory pattern
  - Handles DbContext pooling
  - Integrates interceptor
  - Provides initialization hooks

## Warnings You Might See

During initialization, you may see:

```
Ignoring inability to install OPFS sqlite3_vfs: Cannot install OPFS:
Missing SharedArrayBuffer and/or Atomics...
```

**This is harmless and expected.** The sqlite3.wasm library tries to auto-install the deprecated OPFS VFS (which requires COOP/COEP headers), but we:
1. Filter out this warning in `printErr`
2. Use SAHPool VFS instead (doesn't require special headers)

## Alternative Approaches Considered

### Option: Use Only sqlite3.wasm

**Rejected** because:
- Would lose EF Core integration
- Would need custom LINQ provider
- Would lose migrations, change tracking, etc.

### Option: Custom OPFS Implementation

**Rejected** because:
- High complexity and testing burden
- Would lose SAHPool's transaction safety
- Would likely have edge-case bugs

### Option: Extract SAHPool VFS

**Not Feasible** because:
- SAHPool is tightly coupled with sqlite3.wasm internals
- Requires C struct bindings (Jaccwabyt)
- No way to inject JavaScript VFS into e_sqlite3.a

## Conclusion

The dual-instance architecture is the **correct pragmatic solution** given the architectural constraints of:
- Blazor WebAssembly (.NET + native SQLite)
- OPFS (JavaScript-only APIs)
- EF Core requirements

This approach provides the best balance of:
- ✅ Feature completeness (full EF Core)
- ✅ Reliability (proven implementations)
- ✅ Performance (modern WASM efficiency)
- ✅ Maintainability (no custom VFS code)
