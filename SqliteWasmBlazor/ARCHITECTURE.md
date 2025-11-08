# SqliteWasmBlazor - Minimal EF Core Provider

## Goal
Drop-in replacement for `Microsoft.EntityFrameworkCore.Sqlite` that uses sqlite-wasm with OPFS SAHPool for persistence.

## Architecture

```
EF Core DbContext
    ↓
SqliteWasmBlazor (minimal ADO.NET)
    ├─ SqliteWasmConnection
    ├─ SqliteWasmCommand
    ├─ SqliteWasmDataReader
    ├─ SqliteWasmParameter
    └─ SqliteWasmTransaction
    ↓
postMessage → Worker Thread
    ↓
sqlite-wasm + OPFS SAHPool
```

## What EF Core Actually Needs

### Core Classes (5 total)
1. **SqliteWasmConnection : DbConnection**
   - `Open()` / `OpenAsync()` - Initialize worker connection
   - `Close()` - Close connection
   - `State` property
   - `ConnectionString` property

2. **SqliteWasmCommand : DbCommand**
   - `ExecuteReader()` / `ExecuteReaderAsync()` - SELECT queries
   - `ExecuteNonQuery()` / `ExecuteNonQueryAsync()` - INSERT/UPDATE/DELETE
   - `ExecuteScalar()` / `ExecuteScalarAsync()` - Single value
   - `CommandText` - SQL string
   - `Parameters` - Parameter collection

3. **SqliteWasmDataReader : DbDataReader**
   - `Read()` - Move to next row
   - `GetValue()`, `GetInt32()`, `GetString()`, etc.
   - `FieldCount`, `GetName()`, `GetFieldType()`
   - Holds result rows from worker

4. **SqliteWasmParameter : DbParameter**
   - `ParameterName`, `Value`, `DbType`

5. **SqliteWasmTransaction : DbTransaction**
   - `Commit()` - Execute "COMMIT"
   - `Rollback()` - Execute "ROLLBACK"

### Optional (for full compatibility)
6. **SqliteWasmProviderFactory : DbProviderFactory**
   - Factory methods for creating instances

## Message Protocol (C# ↔ Worker)

### Request Format
```typescript
interface SqlRequest {
    id: number;
    sql: string;
    params?: Array<{ name: string, value: any, type: string }>;
}
```

### Response Format
```typescript
interface SqlResponse {
    id: number;
    success: boolean;
    rows?: Array<any>;           // For SELECT
    rowsAffected?: number;       // For INSERT/UPDATE/DELETE
    lastInsertId?: number;
    error?: string;
}
```

## Worker Implementation

```typescript
import sqlite3InitModule from '@sqlite.org/sqlite-wasm';

// Initialize sqlite-wasm with OPFS
const sqlite3 = await sqlite3InitModule({
    vfs: 'opfs-sahpool'  // Use synchronous OPFS
});

// Execute SQL from main thread
onmessage = async (event) => {
    const { id, sql, params } = event.data;
    const db = sqlite3.oo1.DB('/mydb.sqlite3', 'cw');

    try {
        const result = db.exec({
            sql: sql,
            bind: params,
            returnValue: 'resultRows'
        });

        postMessage({
            id: id,
            success: true,
            rows: result
        });
    } catch (error) {
        postMessage({
            id: id,
            success: false,
            error: error.message
        });
    }
};
```

## Key Design Decisions

### Why Minimal?
- System.Data.SQLite has 445 P/Invoke functions for features we don't need:
  - ❌ Custom functions (sqlite-wasm handles this)
  - ❌ Virtual tables
  - ❌ Encryption/SEE
  - ❌ Backup API (use sqlite-wasm's backup)
  - ❌ Native connection pooling

### Why Worker Thread?
- OPFS SAHPool requires Web Worker
- Synchronous file I/O only available in workers
- Best performance for persistence

### Why SQL-Level?
- sqlite-wasm already has complete implementation
- No need to wrap 445 C functions
- EF Core generates SQL strings
- Clean separation of concerns

## Implementation Size
- **Estimated LOC**: ~800 lines total
  - ADO.NET classes: ~500 lines
  - TypeScript worker: ~200 lines
  - Message bridge: ~100 lines

Compare to System.Data.SQLite: ~50,000 lines!

## Drop-in Replacement Usage

```csharp
// Before (Microsoft.EntityFrameworkCore.Sqlite)
services.AddDbContext<MyContext>(options =>
    options.UseSqlite("Data Source=mydb.db"));

// After (SqliteWasmBlazor)
services.AddDbContext<MyContext>(options =>
    options.UseSqlite("Data Source=mydb.db"));  // Same!
```

EF Core will automatically use our provider because we register it with the correct invariant name: `System.Data.SQLite`
