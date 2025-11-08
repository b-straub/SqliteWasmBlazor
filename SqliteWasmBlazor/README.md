# SqliteWasm.Data

ADO.NET provider for SQLite running via WebAssembly with OPFS persistence.

## Architecture

This project is based on **System.Data.SQLite** (Public Domain) with one key modification:

- **Original**: P/Invoke to native `sqlite3.dll` → Emscripten MEMFS
- **SqliteWasm.Data**: JSImport to Web Worker → sqlite-wasm + OPFS SAHPool

## What We Keep from System.Data.SQLite

✅ All ADO.NET implementation (Connection, Command, DataReader, etc.)
✅ All SQLite-specific logic and type conversions
✅ Connection pooling, transaction handling, parameter binding
✅ BLOB support, backup, all SQLite features

## What We Replace

❌ `UnsafeNativeMethods.cs` (P/Invoke declarations)
✅ `SqliteWasmInterop.cs` (JSImport/JSExport to Worker)

## Benefits

- **Direct OPFS access** - Data persisted natively in browser
- **No MEMFS copy** - Saves memory for large databases
- **Async-first** - Natural for browser environment
- **EF Core compatible** - Works with Entity Framework Core

## File Sources

All `.cs` files copied from System.Data.SQLite v2.0.3 (trunk, 2025-10-30)

| File | Purpose |
|------|---------|
| SQLiteConnection.cs | Connection management |
| SQLiteCommand.cs | SQL command execution |
| SQLiteDataReader.cs | Result set reading |
| SQLiteParameter.cs | Parameter binding |
| SQLiteConvert.cs | Type conversions |
| SQLiteBase.cs | Base SQLite implementation |
| ... | See System.Data.SQLite for full docs |

## Implementation Status

- [ ] Create SqliteWasmInterop.cs with JSImport methods
- [ ] Create Worker with sqlite-wasm + OPFS
- [ ] Update namespaces from System.Data.SQLite → SqliteWasm.Data
- [ ] Test basic connection and queries
- [ ] Build EF Core provider on top

## License

- **System.Data.SQLite code**: Public Domain
- **SqliteWasm.Data additions**: MIT License
