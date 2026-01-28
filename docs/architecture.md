# Architecture

SqliteWasmBlazor uses a worker-based architecture to bridge EF Core with OPFS-backed SQLite.

## The Innovation: Worker-Based Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                   Blazor WebAssembly                        │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │              EF Core DbContext                        │  │
│  │   Migrations • LINQ • Relationships • Tracking        │  │
│  └─────────────────────┬─────────────────────────────────┘  │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │        SqliteWasmBlazor ADO.NET Provider              │  │
│  │    Connection • Command • DataReader • Transaction    │  │
│  └─────────────────────┬─────────────────────────────────┘  │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │         .NET SQLite Stub (8KB e_sqlite3.a)            │  │
│  │           Minimal shim - forwards to Worker           │  │
│  └─────────────────────┬─────────────────────────────────┘  │
│                        │                                    │
│                        │ Request (JSON)                     │
│                        │ SQL + Parameters (~1KB)            │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │          Web Worker (sqlite-worker.ts)                │  │
│  ├───────────────────────────────────────────────────────┤  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │       SQLite Engine (sqlite-wasm)               │  │  │
│  │  │  • Executes ALL SQL queries                     │  │  │
│  │  │  • Handles transactions, indexes, joins         │  │  │
│  │  │  • Direct OPFS SAHPool VFS access               │  │  │
│  │  └──────────────────┬──────────────────────────────┘  │  │
│  │                     ▼                                 │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │    OPFS SAHPool VFS (Persistent Storage)        │  │  │
│  │  │  • Real filesystem API (not emulated)           │  │  │
│  │  │  • Synchronous access handles                   │  │  │
│  │  │  • /databases/YourDb.db                         │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
│                        │                                    │
│                        │ Response (MessagePack)             │
│                        │ Results + Metadata (~60% smaller)  │
│                        ▼                                    │
│  ┌───────────────────────────────────────────────────────┐  │
│  │                 Back to EF Core                       │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

## How It Works

This architecture bridges EF Core with OPFS-backed SQLite:

1. **EF Core needs .NET ADO.NET** - The official `DbConnection` interface for database operations
2. **OPFS needs Web Worker** - Synchronous file access (SAHPool) only available in Web Workers
3. **Workers can't run .NET** - Web Workers cannot execute the main .NET runtime

**Solution:** Minimal native stub + Worker-based SQLite:
- **.NET Stub** (Main thread): Tiny 8KB shim implementing `DbConnection` interface, forwards to Worker
- **SQLite Engine** (Web Worker): Full sqlite-wasm executes queries directly on OPFS SAHPool

**Communication Protocol:**
- **Requests (.NET → Worker)**: JSON serialized (SQL string + parameters) - typically < 1KB
- **Responses (Worker → .NET)**: MessagePack serialized (query results) - optimized for large datasets

All SQL queries execute in the Worker thread against the OPFS-backed database file.

## Why MessagePack?

Standard Blazor JS interop marshalling has significant overhead for large data transfers. Query results can contain thousands of rows, making efficient serialization critical.

**The Problem with JSON/JS Interop:**
- JSON parsing is CPU-intensive for large datasets
- String-based format adds ~40% size overhead
- Binary data (BLOBs) requires base64 encoding (33% larger)
- JS interop marshalling creates intermediate copies

**MessagePack Solution:**
- Binary format is ~60% smaller than JSON
- Faster serialization/deserialization
- Native binary data support (no base64)
- Transferred as `Uint8Array` directly to .NET `byte[]`

```
Asymmetric Protocol:
┌─────────────┐                      ┌─────────────┐
│   .NET      │  ──── JSON ────────▶ │   Worker    │
│   (small)   │  SQL + params <1KB   │             │
│             │                      │             │
│             │  ◀── MessagePack ─── │             │
│   (large)   │  Results optimized   │             │
└─────────────┘                      └─────────────┘
```

This asymmetric approach optimizes for the common case: small requests, large responses.

## Technical Details

### Package Size (Published/Release Build)

- **SqliteWasmBlazor.wasm**: 88 KB (ADO.NET provider + EF Core integration)
- **sqlite-wasm-worker.js**: 234 KB (minified, includes MessagePack)
- **sqlite-wasm-bridge.js**: 1.7 KB (main thread bridge)
- **sqlite3.wasm**: 836 KB (official SQLite WebAssembly build)
- **Total overhead**: ~1.16 MB (compressed sizes are typically 40-50% smaller)

### Performance Characteristics

- **Initial Load**: ~100-200ms (worker initialization + OPFS setup)
- **Query Execution**: < 1ms for simple queries, 10-50ms for complex joins
- **Persistence**: Automatic after `SaveChanges()`, ~10-30ms overhead
- **Database Size**: Limited only by OPFS quota (typically several GB per origin)

### SQLite Configuration

Automatically configured for OPFS environment (SQLite 3.47+):

```sql
PRAGMA locking_mode = exclusive;  -- Required for WAL mode with OPFS
PRAGMA journal_mode = WAL;        -- Write-Ahead Logging for performance
PRAGMA synchronous = FULL;        -- Maximum data safety
```

**Note**: WAL mode with OPFS requires exclusive locking (single connection). This is automatically handled - no concurrency concerns in single-user browser environment.

### Custom EF Core Functions

All EF Core functions are implemented for full compatibility:

- **Arithmetic**: `ef_add`, `ef_divide`, `ef_multiply`, `ef_mod`, `ef_negate`
- **Comparison**: `ef_compare`
- **Aggregates**: `ef_sum`, `ef_avg`, `ef_min`, `ef_max` (optimized via native SQLite)
- **Pattern Matching**: `regexp` (for `Regex.IsMatch()`)
- **Collation**: `EF_DECIMAL` (for proper decimal sorting)

## Why This Matters

Traditional Blazor WASM database solutions have significant limitations:

| Solution | Storage | Persistence | EF Core | Limitations |
|----------|---------|-------------|---------|-------------|
| **InMemory** | RAM | None | Full | Lost on refresh |
| **IndexedDB** | IndexedDB | Yes | Limited | No SQL, complex API |
| **SQL.js** | IndexedDB | Yes | None | Manual serialization |
| **besql** | Cache API | Yes | Partial | Emulated filesystem |
| **SqliteWasmBlazor** | **OPFS** | **Yes** | **Full** | **None!** |

**besql** uses Cache Storage API to emulate a filesystem. SqliteWasmBlazor uses **real OPFS filesystem** with synchronous access, providing true native-like performance and the ability to run the actual .NET SQLite provider.
