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
│  │        .NET SQLite Stub (10 KB e_sqlite3.a)           │  │
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

Three constraints meet and leave exactly one shape:

1. EF Core needs a .NET `DbConnection`.
2. OPFS synchronous access handles exist only inside a Web Worker.
3. A Web Worker cannot run the .NET runtime.

So the engine goes where the filesystem is, and .NET keeps only the interface.
`e_sqlite3.a` is replaced by a 10 KB stub that implements the P/Invoke surface
`Microsoft.Data.Sqlite` expects and forwards across the worker boundary; every
statement is actually planned and executed by sqlite-wasm in the worker,
directly against the OPFS SAHPool. Nothing is copied to a scratch filesystem
first, and there is no second SQLite instance on the main thread.

## Why MessagePack?

The protocol is deliberately asymmetric because the traffic is: a request is a
SQL string and its parameters, a response can be thousands of rows.

JSON is fine going out and expensive coming back — parsing dominates on large
result sets, the string encoding costs ~40%, and BLOBs have to be base64'd for
another 33% on top. MessagePack is ~60% smaller, carries binary natively, and
arrives as a `Uint8Array` that becomes a .NET `byte[]` without an intermediate
copy.

## Technical Details

### Package Size (Published/Release Build)

| Asset | Release | Compressed |
|-------|---------|------------|
| `SqliteWasmBlazor.wasm` (ADO.NET provider + EF Core integration) | 222 KB | 66 KB br |
| `sqlite-wasm-worker.js` (minified, includes MessagePack) | 302 KB | 89 KB gz |
| `sqlite-wasm-bridge.js` (main thread bridge) | 33 KB | 12 KB gz |
| `sqlite3.wasm` (official SQLite WebAssembly build) | 844 KB | 339 KB br |
| **Total** | **~1.37 MB** | **~0.5 MB** |

`SqliteWasmBlazor.Crypto` replaces the worker and bridge with larger bundles of
its own and adds `crypto-bridge.js`; take it only if you need at-rest encryption.

### Performance Characteristics

- **Initial Load**: ~100-200ms (worker initialization + OPFS setup)
- **Query Execution**: < 1ms for simple queries, 10-50ms for complex joins
- **Persistence**: Automatic after `SaveChanges()`, ~10-30ms overhead
- **Database Size**: Limited only by OPFS quota (typically several GB per origin)

### SQLite Configuration

Automatically configured for OPFS environment (SQLite 3.53.4):

```sql
PRAGMA locking_mode = exclusive;  -- Required for WAL mode with OPFS
PRAGMA journal_mode = WAL;        -- Write-Ahead Logging for performance
PRAGMA synchronous = FULL;        -- Maximum data safety
```

**Note**: WAL mode with OPFS requires exclusive locking (single connection). This is automatically handled - no concurrency concerns in single-user browser environment.

### Custom EF Core Functions

The worker registers the full `ef_*` set — arithmetic, comparison, aggregates,
`regexp`, and the `EF_DECIMAL` collation — so decimal arithmetic and
`Regex.IsMatch()` translate rather than falling back to client evaluation. The
list is in [Advanced Features](advanced-features.md#custom-ef-core-functions).
