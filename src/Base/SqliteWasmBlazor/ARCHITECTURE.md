# SqliteWasmBlazor - Minimal ADO.NET Provider

## Goal
Complete ADO.NET provider for SQLite in Blazor WebAssembly that uses sqlite-wasm with OPFS SAHPool for persistence. Can be used standalone (raw ADO.NET) or with Entity Framework Core.

## Architecture

```
┌─────────────────────────────────────┐
│  Usage Layer (Choose One)          │
├─────────────────────────────────────┤
│  ┌─────────────┐  ┌──────────────┐ │
│  │  EF Core    │  │  Raw ADO.NET │ │
│  │  DbContext  │  │  (Direct SQL)│ │
│  └──────┬──────┘  └──────┬───────┘ │
│         └─────────────────┘         │
│                 ↓                   │
│  ┌──────────────────────────────┐  │
│  │ SqliteWasmBlazor (ADO.NET)   │  │
│  ├──────────────────────────────┤  │
│  │  SqliteWasmConnection        │  │
│  │  SqliteWasmCommand           │  │
│  │  SqliteWasmDataReader        │  │
│  │  SqliteWasmParameter         │  │
│  │  SqliteWasmTransaction       │  │
│  └──────────────┬───────────────┘  │
│                 ↓                   │
│      postMessage → Worker Thread   │
│                 ↓                   │
│     sqlite-wasm + OPFS SAHPool     │
└─────────────────────────────────────┘
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

## Usage Patterns

### Option 1: With Entity Framework Core (Drop-in Replacement)

```csharp
// Program.cs
var builder = WebAssemblyHostBuilder.CreateDefault(args);

services.AddDbContextFactory<MyContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=mydb.db");
    options.UseSqliteWasm(connection);
});

var host = builder.Build();

// Initialize with automatic migrations
await host.Services.InitializeSqliteWasmDatabaseAsync<MyContext>();

await host.RunAsync();
```

### Option 2: Standalone ADO.NET (No EF Core)

```csharp
// Program.cs
var builder = WebAssemblyHostBuilder.CreateDefault(args);

var host = builder.Build();

// Initialize worker bridge only
await host.Services.InitializeSqliteWasmAsync();

await host.RunAsync();
```

```csharp
// Component code
await using var connection = new SqliteWasmConnection("Data Source=mydb.db");
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT * FROM Users WHERE Id = $id";
command.Parameters.Add(new SqliteWasmParameter { ParameterName = "$id", Value = 42 });

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    var name = reader.GetString(1);
    // Process results...
}
```

### Option 3: Direct Worker Bridge (Low-Level)

```csharp
// Program.cs - same as Option 2
await host.Services.InitializeSqliteWasmAsync();
```

```csharp
// Component code
@inject SqliteWasmWorkerBridge WorkerBridge

await WorkerBridge.OpenDatabaseAsync("mydb.db");

var result = await WorkerBridge.ExecuteSqlAsync(
    "mydb.db",
    "SELECT * FROM Users WHERE Id = $0",
    new Dictionary<string, object?> { ["$0"] = 42 },
    CancellationToken.None
);

foreach (var row in result.Rows)
{
    // Process row data...
}
```

## When to Use Each Pattern

| Pattern | Use When | Benefits | Complexity |
|---------|----------|----------|------------|
| **EF Core** | Complex domain models, migrations needed | LINQ, change tracking, relationships | High |
| **ADO.NET** | Simple CRUD, porting existing code | Full SQL control, lightweight | Medium |
| **Worker Bridge** | Custom scenarios, bulk operations | Maximum control, lowest overhead | Low |
