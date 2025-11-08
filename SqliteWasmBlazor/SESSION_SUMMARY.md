# Session Summary: SqliteWasm.Data Implementation Plan

## Session Date
November 3, 2025

## Goal
Replace P/Invoke layer in System.Data.SQLite with JSImport to enable EF Core with sqlite-wasm + OPFS persistence in Blazor WASM.

## Journey & Key Insights

### Initial Exploration (The Wrong Paths)
1. **First attempt**: Tried to replace ISQLite3Provider (~80 methods) with batching logic
   - Complexity: HIGH - would need to intercept and batch many low-level calls

2. **Second attempt**: Considered adapting MySqlConnector
   - Problem: MySQL → SQLite translation would be significant work

3. **Third attempt**: Considered adapting Pomelo for ADO.NET layer
   - Problem: Pomelo is EF Core provider, not ADO.NET

### The Breakthrough
**Realization**: System.Data.SQLite already has ALL the SQLite-specific ADO.NET logic!
- We only need to replace ONE file: `UnsafeNativeMethods.cs` (P/Invoke declarations)
- Everything else (connection management, type conversions, transactions) stays intact

## Final Architecture

```
System.Data.SQLite (Public Domain)
├─ Keep: All 15 ADO.NET implementation files
├─ Keep: SQLite-specific logic, type handling
└─ Replace: UnsafeNativeMethods.cs
           ↓
    SqliteWasmInterop.cs (JSImport/JSExport)
           ↓
    Worker: sqlite-wasm + OPFS SAHPool VFS
```

## Repositories Cloned

1. **MySqlConnector** (`/Users/berni/Projects/MySqlConnector`)
   - MIT License
   - Reference for ADO.NET patterns

2. **Pomelo.EntityFrameworkCore.MySql** (`/Users/berni/Projects/Pomelo.EntityFrameworkCore.MySql`)
   - MIT License
   - Will adapt for EF Core provider later

3. **System.Data.SQLite** (`/Users/berni/Projects/System.Data.SQLite`)
   - Public Domain
   - v2.0.3 (trunk, 2025-10-30)
   - **Primary source for SqliteWasm.Data**

## What We Created

### Project Structure
```
/Users/berni/Projects/SQLiteNET/SqliteWasm.Data/
├── SqliteWasm.Data.csproj          ✅ Created
├── README.md                        ✅ Created
├── SqliteWasmInterop.cs            ✅ Created (stub)
├── SESSION_SUMMARY.md              ✅ Created (this file)
└── [15 System.Data.SQLite files]   ✅ Copied

Files Copied from System.Data.SQLite:
├── SQLiteConnection.cs             (connection management)
├── SQLiteCommand.cs                (command execution)
├── SQLiteDataReader.cs             (result reading)
├── SQLiteParameter.cs              (parameter binding)
├── SQLiteParameterCollection.cs    (parameter collections)
├── SQLiteTransaction.cs            (transactions)
├── SQLiteException.cs              (error handling)
├── SQLiteFactory.cs                (ADO.NET factory)
├── SQLiteConvert.cs                (type conversions)
├── SQLiteConnectionStringBuilder.cs
├── SQLiteDataAdapter.cs
├── SQLiteCommandBuilder.cs
├── SQLiteBase.cs                   (base implementation)
├── SQLiteStatement.cs
├── SQLiteKeyReader.cs
├── HelperMethods.cs
├── AssemblyInfo.cs
├── SQLiteConnectionPool.cs
├── SQLiteEnlistment.cs
├── SQLiteLog.cs
├── SQLiteMetaDataCollectionNames.cs
├── SQLiteBackup.cs
└── SQLiteBlob.cs
```

### SqliteWasmInterop.cs
Created stub with JSImport declarations for core SQLite functions:
- Connection: `sqlite3_open`, `sqlite3_close`
- Statements: `sqlite3_prepare_v2`, `sqlite3_step`, `sqlite3_finalize`, `sqlite3_reset`
- Binding: `sqlite3_bind_*` (null, int64, double, text, blob)
- Column access: `sqlite3_column_*` (count, name, type, values)
- Error handling: `sqlite3_errmsg`, `sqlite3_errcode`
- Transactions: `sqlite3_last_insert_rowid`, `sqlite3_changes`

**Total P/Invoke to replace**: ~445 DllImport declarations from UnsafeNativeMethods.cs

## Next Steps

### Immediate Tasks
1. **Update Namespaces** - Change `System.Data.SQLite` → `SqliteWasm.Data` in all files
2. **Wire Interop** - Update `SQLiteBase.cs` to use `SqliteWasmInterop` instead of `UnsafeNativeMethods`
3. **Create Worker** - TypeScript worker with sqlite-wasm + OPFS initialization
4. **Implement JSExport** - Worker-side functions matching JSImport declarations
5. **Test Basic Flow** - Connection → Query → Close

### Medium Term
6. **Complete JSImport Coverage** - Implement all ~445 functions incrementally
7. **Error Handling** - Map SQLite error codes properly
8. **Memory Management** - Handle JSObject lifetimes correctly
9. **Transaction Support** - Test BEGIN, COMMIT, ROLLBACK
10. **BLOB Support** - Test large binary data

### Long Term
11. **EF Core Provider** - Adapt Pomelo.EntityFrameworkCore.MySql
12. **Performance Testing** - Benchmark vs current MEMFS/OPFS bridge
13. **Contribute Back** - Offer WASM support to System.Data.SQLite maintainers

## Key Technical Decisions

1. **License Strategy**
   - System.Data.SQLite code: Public Domain (keep as-is)
   - Our additions (SqliteWasmInterop, Worker): MIT License

2. **Namespace**
   - Changed from `System.Data.SQLite` to `SqliteWasm.Data`
   - Avoids conflicts with official System.Data.SQLite

3. **Target Framework**
   - .NET 10.0 (latest)
   - Browser platform only

4. **Async Strategy**
   - JSImport methods are `async Task<T>`
   - System.Data.SQLite expects sync
   - Will use `Task.GetAwaiter().GetResult()` or rework async support

## Benefits of This Approach

✅ **Minimal Code Changes** - Only replace interop layer
✅ **Battle-Tested Logic** - Keep all System.Data.SQLite's proven implementation
✅ **SQLite-Specific** - No need to translate from MySQL
✅ **Direct OPFS** - Native browser persistence
✅ **Lower Memory** - No MEMFS/OPFS dual storage
✅ **EF Core Compatible** - Build EF provider on top
✅ **Contribution Path** - Can offer back to System.Data.SQLite

## References

- System.Data.SQLite: https://system.data.sqlite.org/
- sqlite-wasm: https://sqlite.org/wasm
- JSImport/JSExport: https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/import-export-interop
- OPFS SAHPool: https://developer.mozilla.org/en-US/docs/Web/API/File_System_API/Origin_private_file_system

## Session Conclusion

We've successfully identified the optimal approach and set up the foundational structure. The key insight was recognizing that System.Data.SQLite already contains all the complex SQLite logic we need - we just need to swap out the native interop for WASM interop.

**Estimated Effort Remaining:**
- Basic functionality: 2-3 days (namespace updates, basic worker, core functions)
- Full JSImport coverage: 1-2 weeks (445 functions)
- EF Core provider: 1-2 weeks (adapt Pomelo)
- Testing & refinement: 1 week

**Total: ~4-6 weeks for production-ready implementation**
