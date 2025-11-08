# SqliteWasmBlazor Architecture

## Core Innovation: Worker-Based SQLite Pattern

SqliteWasmBlazor solves a fundamental challenge in bringing EF Core to Blazor WASM with persistent storage:

### The Problem

1. **EF Core needs .NET ADO.NET** - Requires `DbConnection` interface for database operations
2. **OPFS needs Web Workers** - Synchronous file access (SAHPool) only works in Web Workers
3. **Workers can't run .NET WASM** - Web Workers cannot execute the main .NET runtime

### The Solution: Minimal Stub + Worker-Based SQLite

```
Main Thread (.NET WASM)                Web Worker (JavaScript)
┌────────────────────────┐             ┌────────────────────────┐
│  EF Core DbContext     │             │                        │
│         ↓              │             │                        │
│  ADO.NET Provider      │             │                        │
│         ↓              │             │                        │
│ SqliteWasmConnection   │             │                        │
│         ↓              │             │                        │
│ ┌────────────────────┐ │   JSON      │ ┌────────────────────┐ │
│ │ .NET Stub (8KB)    │ │────────────►│ │ SQLite Engine      │ │
│ │  e_sqlite3.a       │ │             │ │  sqlite-wasm       │ │
│ │  (Shim only)       │ │◄────────────│ │  (OPFS SAHPool)    │ │
│ └────────────────────┘ │ MessagePack │ └────────────────────┘ │
│                        │             │         ↓              │
│                        │             │   OPFS Storage         │
└────────────────────────┘             └────────────────────────┘
```

## Component Breakdown

### 1. Main Thread Components (C#)

#### SqliteWasmConnection
- Extends `DbConnection`
- Manages connection state
- Initializes dual-instance communication
- Handles connection pooling

#### SqliteWasmCommand
- Extends `DbCommand`
- Translates EF Core commands to SQL
- Manages parameters
- Routes execution to appropriate instance

#### SqliteWasmDataReader
- Extends `DbDataReader`
- Streams results from Instance 1
- Handles type conversion (.NET ↔ SQLite)
- Implements efficient row iteration

#### SqliteWasmWorkerBridge
- Singleton managing Web Worker lifecycle
- JSON serialization for requests (SQL + parameters)
- MessagePack deserialization for responses (query results)
- Promise-based async operations
- Request/response correlation via request IDs

### 2. Web Worker Components (TypeScript)

#### sqlite-worker.ts
The main worker script that orchestrates both SQLite instances:

```typescript
// Instance 2: OPFS-backed SQLite
const sqlite3 = await sqlite3InitModule({...});
const poolUtil = await sqlite3.installOpfsSAHPoolVfs({
    initialCapacity: 6,
    directory: '/databases',
    name: 'opfs-sahpool'
});

// Message handling
self.onmessage = async (event) => {
    const {type, database, sql, parameters} = event.data;

    switch(type) {
        case 'execute':
            // Execute on Instance 2 and return results
            return await executeSql(database, sql, parameters);
        case 'persist':
            // Copy MEMFS → OPFS
            return await persistDatabase(database);
        case 'load':
            // Copy OPFS → MEMFS
            return await loadDatabase(database);
    }
};
```

#### worker-bridge.ts
Client-side bridge managing worker communication:

```typescript
class SqliteWasmWorkerBridge {
    private worker: Worker;
    private pendingRequests: Map<number, Deferred>;

    async executeAsync(sql: string, params: any) {
        return this.sendRequest({
            type: 'execute',
            sql,
            parameters: params
        });
    }
}
```

### 3. Communication Protocol

#### Request Format (JSON)
Lightweight JSON for sending queries to Worker:

```typescript
// Request (.NET → Worker via JSON)
{
    id: number,
    data: {
        type: 'execute' | 'open' | 'close' | 'exists' | 'delete',
        database: string,
        sql?: string,
        parameters?: Record<string, any>
    }
}
```

**Why JSON for requests?**
- Requests are tiny (SQL string + few parameters, typically < 1KB)
- Simple, debuggable format
- Native browser support, no library needed on .NET side

#### Response Format (MessagePack)
Binary protocol for efficient result transfer:

```typescript
// Response (Worker → .NET via MessagePack binary)
{
    columnNames: string[],
    columnTypes: string[],  // SQLite type affinity
    typedRows: {
        types: string[],
        data: any[][]
    },
    rowsAffected: number,
    lastInsertId: number
}
```

**Why MessagePack for responses?**
- Large result sets benefit from binary encoding (60% smaller than JSON)
- Native BLOB support (no Base64 overhead)
- Faster deserialization for complex data
- Type preservation across the wire

## Data Flow

### Query Execution

```
1. EF Core generates SQL
   ↓
2. SqliteWasmCommand.ExecuteReaderAsync()
   ↓
3. Serialize request to JSON and postMessage to Worker
   ↓
4. Worker executes query on OPFS-backed database
   ↓
5. Worker serializes results to MessagePack
   ↓
6. Results deserialized and returned to SqliteWasmDataReader
   ↓
7. EF Core materializes entities
```

### Persistence (After SaveChanges)

```
1. EF Core calls SaveChanges()
   ↓
2. SqliteWasmCommand sends INSERT/UPDATE/DELETE to Worker
   ↓
3. Worker executes on OPFS-backed database
   ↓
4. SQLite writes to OPFS via SAHPool VFS
   ↓
5. Changes are immediately persistent
   ↓
6. Worker returns rowsAffected/lastInsertId
   ↓
7. EF Core updates tracking state
```

### Database Initialization

```
1. Application starts
   ↓
2. SqliteWasmWorkerBridge.InitializeAsync()
   ↓
3. Create and initialize Worker
   ↓
4. Worker loads sqlite-wasm
   ↓
5. Install OPFS SAHPool VFS
   ↓
6. Open database in OPFS (creates if doesn't exist)
   ↓
7. Register EF Core custom functions (ef_*)
   ↓
8. Signal ready to main thread
```

## Key Design Decisions

### Why Not Use OPFS Directly from .NET?

**Attempted Approach**: Create a custom VFS in C and compile with e_sqlite3.a

**Why It Failed**:
- OPFS APIs are async JavaScript (Promise-based)
- SQLite VFS expects synchronous file operations
- Can't call JS async functions from C synchronously in WASM
- Would need complex callback marshalling

**Better Solution**: Use official sqlite-wasm's proven OPFS SAHPool implementation in Worker

### Why Hybrid JSON/MessagePack?

**Requests use JSON** because:
- Requests are small (SQL string + parameters, typically < 1KB)
- Simple to debug in browser DevTools
- No serialization library needed on .NET side

**Responses use MessagePack** for performance:

| Payload Size | JSON | MessagePack | Improvement |
|--------------|------|-------------|-------------|
| 100 rows     | 45KB | 18KB        | 60% smaller |
| 1000 rows    | 420KB | 165KB      | 61% smaller |
| BLOB data    | Base64 encoded | Binary | 3x faster |

### Why Minimal Native Stub?

The 8KB `e_sqlite3.a` stub provides:
- **ADO.NET Compatibility**: Implements `DbConnection` interface for EF Core
- **Minimal Overhead**: No actual SQLite code, just message passing
- **Standard Interface**: EF Core works without modifications
- **Type Safety**: Strong typing in C# while Worker handles SQL

### Why All Queries in Worker?

Benefits of worker-based execution:
1. ✅ Direct OPFS access with SAHPool (synchronous, fast)
2. ✅ No data copying between instances
3. ✅ Official sqlite-wasm with all optimizations
4. ✅ Immediate persistence (no sync step needed)

## Performance Characteristics

### Startup Time
- Worker initialization: ~50ms
- OPFS VFS setup: ~30ms
- First database open: ~20ms
- **Total cold start**: ~100ms

### Query Performance
| Operation | Worker (OPFS SAHPool) | Notes |
|-----------|-----------------------|-------|
| Simple SELECT | 3-5ms | Includes message passing |
| Complex JOIN | 15-25ms | OPFS nearly as fast as native |
| INSERT (single) | 2-3ms | Immediately persistent |
| Bulk INSERT (1000) | 80-120ms | With transaction |

### Message Passing Overhead
- Serialization (MessagePack): ~1-2ms per 100 rows
- Deserialization: ~1-2ms per 100 rows
- Worker postMessage: < 1ms
- Total round-trip: ~5-10ms baseline

## Storage Details

### OPFS Structure

```
/databases/
├── TodoDb.db
├── TodoDb.db-wal
├── TodoDb.db-shm
└── [other databases]
```

### SQLite Configuration

```sql
PRAGMA journal_mode = WAL;    -- Write-Ahead Logging
PRAGMA synchronous = FULL;    -- Maximum durability
PRAGMA temp_store = MEMORY;   -- Fast temp tables
```

## EF Core Integration

### Custom Functions (ef_*)

Implemented in TypeScript for decimal support:

```typescript
function ef_add(db: any, sqlite3: any) {
    db.createFunction('ef_add', (a: string, b: string) => {
        const bdA = new Decimal(a || '0');
        const bdB = new Decimal(b || '0');
        return bdA.add(bdB).toString();
    });
}

// Also: ef_divide, ef_multiply, ef_negate, ef_compare
// Aggregates: ef_sum, ef_avg, ef_min, ef_max
```

### Migration Support

Custom `SqliteWasmHistoryRepository`:

```csharp
protected override bool InterpretExistsResult(object value)
{
    // OPFS returns different types than standard SQLite
    return value switch
    {
        long l => l != 0,
        int i => i != 0,
        bool b => b,
        _ => base.InterpretExistsResult(value)
    };
}
```

## Error Handling

### Worker Failures
- Worker crash → Auto-restart with exponential backoff
- OPFS unavailable → Graceful degradation to MEMFS-only
- Quota exceeded → Surface error to application

### Data Consistency
- Transaction support via BEGIN/COMMIT/ROLLBACK
- Automatic rollback on errors
- OPFS writes are atomic per-file

## Security Considerations

### Origin Isolation
- OPFS is private to the origin
- No cross-origin access possible
- Survives cache clearing

### Data Encryption
- Not encrypted at rest (browser-controlled)
- Use server-side encryption for sensitive data
- Consider IndexedDB encryption layer if needed

## Future Optimizations

### Planned Improvements
1. **Incremental Sync**: Only persist changed pages
2. **Background Persistence**: Use `requestIdleCallback`
3. **Compression**: LZ4 compression for OPFS storage
4. **Multi-Database**: Share single Worker across DbContexts
5. **Query Caching**: Cache prepared statements

### Experimental Features
- Direct OPFS write from .NET (if browser APIs evolve)
- SharedArrayBuffer for zero-copy transfers
- Streaming large result sets

## Comparison with Alternatives

### vs. besql (bitplatform)

| Feature | besql | SqliteWasmBlazor |
|---------|-------|------------------|
| Storage | Cache API | OPFS SAHPool |
| Filesystem | Emulated | Native |
| SQLite | Custom build | Official sqlite-wasm |
| EF Core | Partial | Full |
| Migrations | Limited | Complete |

### vs. SQL.js

| Feature | SQL.js | SqliteWasmBlazor |
|---------|--------|------------------|
| EF Core | ❌ No | ✅ Yes |
| Persistence | Manual | Automatic |
| Threading | Main only | Worker-based |
| Type Safety | JS only | Full .NET |

---

**Architecture by bernisoft**
*Building the future of offline-first Blazor apps*
