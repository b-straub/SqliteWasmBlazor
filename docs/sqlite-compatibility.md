# SQLite Compatibility Notes

SqliteWasmBlazor targets the async EF Core and ADO.NET surface used by Blazor
WebAssembly applications. The goal is to make normal EF Core code behave like
native SQLite without app-level `OperatingSystem.IsBrowser()` branches.

## Covered Behavior

The browser test app exercises these SQLite/EF areas through
`SqliteWasmBlazor.Tests`:

- CRUD, bulk inserts, FTS5 search, auxiliary ranking/highlighting/snippet
  functions, FTS5 vocabulary tables, RTree virtual tables, dbstat virtual
  tables, and soft-delete cleanup.
- EF Core bulk `ExecuteUpdateAsync` and `ExecuteDeleteAsync`.
- EF Core raw SQL query and command APIs, including `FromSqlInterpolated`,
  `ExecuteSqlInterpolatedAsync`, and scalar `SqlQuery`.
- EF Core migrations, migration history tracking, and recovery diagnostics.
- Relationships, includes, cascade delete, joins, nullable values, and GUID keys.
- SQLite foreign-key enforcement (`PRAGMA foreign_keys = ON`) and
  database-side cascade deletes.
- Type marshalling for integers, blobs, strings, Unicode, `DateTimeOffset`,
  `TimeSpan`, `char`, nullable values, JSON collections, and GUID byte arrays.
- EF Core decimal scalar/aggregate function names mapped to SQLite behavior,
  with fixed-scale worker arithmetic, aggregate, comparison, and collation
  behavior for values beyond JavaScript's binary floating-point precision.
- Raw database import/export, backup/restore on import failure, re-open flows,
  and sequential imports.
- ADO.NET async command execution: DDL, parameter binding, non-query, scalar,
  reader, blobs, nulls, commit, rollback, plus explicit failures for unsupported
  sync command APIs.
- ADO.NET command-type validation: `CommandType.Text` is supported, while
  `StoredProcedure` and `TableDirect` fail explicitly instead of being treated
  as SQL text.
- ADO.NET `DateOnly` and `TimeOnly` parameters bind explicitly as ISO TEXT and
  round-trip through typed reader APIs.
- EF Core `DateOnly` and `TimeOnly` SQL parameters bind as SQLite TEXT and
  round-trip through scalar SQL queries without app-level browser branches.
  `DateOnly` and `TimeOnly` member translations such as additive methods,
  `DayNumber`, date parts, time parts, and `IsBetween` are covered through
  composed scalar SQL queries.
- SQLite constraint and conflict behavior through ADO.NET, including `UNIQUE`,
  `NOT NULL`, `INSERT OR IGNORE`, `ON CONFLICT DO UPDATE`, and rows-affected
  semantics.
- Native rows-affected semantics for DML with leading SQL comments, DML
  preceded by `WITH` CTEs, and `REPLACE`.
- Native reader `RecordsAffected` semantics for row-producing `SELECT` and
  `PRAGMA` statements (`-1` instead of a DML count).
- SQLite `RETURNING` clauses through ADO.NET scalar and reader APIs, including
  returned column metadata and `RecordsAffected` for insert/update/delete.
- ADO.NET reader metadata for empty result sets, including `FieldCount`,
  `GetName`, and `GetDataTypeName` for empty `SELECT` and zero-row
  `UPDATE ... RETURNING`.
- ADO.NET `GetFieldValue<DateOnly>` and `GetFieldValue<TimeOnly>` conversions
  for SQLite text storage, plus Julian-day `DateOnly`, `DateTime`, and
  `DateTimeOffset` conversion.
- ADO.NET `GetFieldValue<T>` conversions for common native reader types:
  booleans, integer widths, floating point, decimal, `DateTime`, `Guid`, and
  nullable reference values.
- ADO.NET schema metadata via `GetFieldType` before `Read()` and
  `GetSchemaTable()` / `GetColumnSchema()` for generic data-loading code.
- ADO.NET reader ordinal lookup parity with `Microsoft.Data.Sqlite`: exact
  column-name matches first, then case-insensitive lookup, with ambiguous
  case-insensitive matches rejected.
- ADO.NET streaming reader access for `GetBytes` and `GetChars`, including
  length probes, chunked reads, end-of-value behavior, `GetStream`,
  `GetTextReader`, and generic `GetFieldValue<Stream/TextReader>`.
- ADO.NET reader command behaviors: `CloseConnection`, `SchemaOnly`, and
  `SingleRow`.
- ADO.NET named parameter compatibility for bare names and SQLite prefixes
  (`@name`, `$name`, `:name`), including packed blob parameters and
  prefix-insensitive collection lookup.
- ADO.NET explicit `DbType` parameter binding for common SQLite storage
  classes: text, integer, real, blob, booleans, decimal text, date/time text,
  and large integers that must avoid JavaScript number precision loss.
- Common SQLite connection-string data-source aliases: `Data Source`,
  `DataSource`, and `Filename`, including quoted values containing semicolons.
- EF Core database lifecycle APIs (`EnsureCreatedAsync`, `EnsureDeletedAsync`,
  `CanConnectAsync`) use the same connection-string data-source parsing as the
  runtime connection.
- Common native SQLite scalar functions through sqlite-wasm, including text,
  numeric, advanced math, date/time, type, formatting, conditional,
  null-handling, likelihood no-op, blob hex/unhex, random blobs, zero-blob,
  octet-length, Unicode quoting, `soundex`, scalar `sha3`, and JSON extraction
  functions. The worker registers compatible `soundex` and `sha3` fallbacks
  because these are optional in SQLite native builds and absent from the
  bundled wasm package.
- Native SQLite date/time functions and modifiers through sqlite-wasm, including
  `date`, `time`, `datetime`, `julianday`, `unixepoch`, `strftime`, `timediff`,
  subsecond handling, weekday/month boundary modifiers, and Unix epoch
  conversion.
- Common native SQLite aggregate and window functions through sqlite-wasm,
  including `count`, filtered aggregates, `sum`, `avg`, `total`, `min`, `max`,
  `group_concat`, `string_agg`, JSON aggregate functions, `row_number`,
  ranking functions, distribution functions, `ntile`, `lag`, `lead`,
  `first_value`, and `last_value`.
- Native SQLite JSON functions and operators through sqlite-wasm, including
  JSON construction, extraction, mutation, validation, quoting, `->`, `->>`,
  `json_each`, `json_tree`, and JSONB extraction/storage probes.
- Native SQLite state and storage-offset functions through sqlite-wasm,
  including `changes`, `total_changes`, `last_insert_rowid`, and
  `sqlite_offset`.
- SQLite runtime metadata and compile-option probes, including
  `sqlite_version`, `sqlite_source_id`, `sqlite_compileoption_used`, and
  `sqlite_compileoption_get`, with FTS5 enabled in the bundled wasm package.
- SQLite table-valued introspection PRAGMAs for runtime capability and schema
  discovery, including `pragma_function_list`, `pragma_module_list`,
  `pragma_compile_options`, `pragma_table_info`, `pragma_table_xinfo`,
  `pragma_foreign_key_list`, `pragma_index_list`, and `pragma_database_list`.
- Common SQLite configuration and health-check PRAGMAs through ADO.NET,
  including `foreign_keys`, `recursive_triggers`, `user_version`,
  `application_id`, `cache_size`, `busy_timeout`, `synchronous`, `temp_store`,
  `journal_mode`, `page_size`, `encoding`, `integrity_check`, and
  `quick_check`.
- Optional native virtual table modules bundled in sqlite-wasm 3.53.0,
  including `fts5`, `fts5vocab`, `rtree`, `rtree_i32`, `dbstat`, `bytecode`,
  `tables_used`, `sqlite_stmt`, and `sqlite_dbpage`.
- Common EF Core query translations for string operations, `EF.Functions.Like`,
  `LIKE ... ESCAPE`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, `Trim`,
  `TrimStart`, `TrimEnd`, trim-character overloads, `IndexOf`, `CompareTo`,
  `IndexOf` with a start offset, char overloads for `Contains`, `StartsWith`,
  `EndsWith`, `IndexOf`, and `Replace`, one- and two-argument `Substring`,
  `string.Compare`, `string.Concat`, first/last character extraction, null
  coalescing, ordering, `Skip`, and `Take`.
- EF Core typed predicates over booleans, nullable booleans, `DateTime`,
  nullable `DateTime`, enums, nullable enums, collection `Contains`/`IN`
  predicates for common scalar types, GUID `IN`, `HasFlag`,
  `GetValueOrDefault`, `Any`, and `All`.
- EF Core `DateTime` member translations over SQLite date functions,
  including `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `DayOfYear`,
  `Millisecond`, `DayOfWeek`, `Date`, `TimeOfDay`, nullable `DateTime`
  members, `DateOnly.FromDateTime`, current-clock methods, additive methods
  including `AddTicks`, grouping, and ordering.
- EF Core grouped aggregates and SQLite set operations, including `GroupBy`,
  `Count`, `LongCount`, `Sum`, `Min`, `Max`, `Average`, `string.Concat`,
  `string.Join`, `Distinct`, `Union`, `Except`, and `Intersect`.
- EF Core SQLite conversion mappings for common `ToString()` casts over
  booleans, integer types, floating point, decimal, `DateTime`,
  `DateTimeOffset`, `TimeSpan`, GUID, BLOB, and char values.
- EF Core math translations over SQLite numeric functions, including `Abs`,
  `Max`, `Min`, `Round`, `Ceiling`, `Floor`, `Pow`, `Sqrt`, trigonometric,
  hyperbolic, logarithmic, floating-point modulo, `Sign`, `Truncate`,
  `DegreesToRadians`, `RadiansToDegrees`, `MathF`, and generic math
  translations. The worker registers compatible fallbacks for the SQLite math
  function family so sqlite-wasm compile-time options do not create
  browser-only query gaps.
- SQLite-specific EF Core database functions: `EF.Functions.Glob`,
  `EF.Functions.Collate`, `EF.Functions.Hex`, `EF.Functions.Unhex`, and
  byte-array `EF.Functions.Substr`. The worker registers a SQLite-compatible
  `unhex` fallback so newer EF Core translations do not depend on the bundled
  sqlite-wasm core version.
- EF Core byte-array translations: `Contains`, `Length`, and `SequenceEqual`.
- Explicit transaction commit/rollback.
- Transaction dispose rollback parity with native SQLite: disposing an
  uncommitted transaction rolls it back and later logical connections wait for
  cleanup before running SQL.
- Concurrent logical EF transactions for the same database: a later `BEGIN`
  waits for the active worker transaction to commit or roll back instead of
  failing with nested-transaction errors.
- SQLite-compatible ADO.NET transaction isolation levels: `Unspecified`,
  `ReadUncommitted`, `ReadCommitted`, `RepeatableRead`, `Serializable`, and
  `Snapshot`; unsupported levels fail explicitly.
- Async transaction savepoints: `CreateSavepointAsync`,
  `RollbackToSavepointAsync`, and `ReleaseSavepointAsync`.

## Native Gaps Closed In This Fork

### Transaction disposal

Native SQLite rolls back an uncommitted transaction when the transaction object
is disposed. The fork now implements this for async disposal and queues cleanup
for synchronous disposal. Async commands and new transactions for the same
database wait for that cleanup before executing.

`Commit()` and `Rollback()` remain unsupported synchronous APIs and now fail
explicitly instead of silently returning success. Use `CommitAsync()` and
`RollbackAsync()`.

### Worker-side logical connection serialization

The browser worker holds one SQLite handle per database, while native SQLite
usually gives each connection its own handle. That difference can otherwise
surface as `cannot start a transaction within a transaction` when two logical
connections reach the same worker handle.

The worker now serializes requests per database and defers later `BEGIN`
requests while a transaction is active. Requests for other databases can still
continue.

This behavior is covered by `Transaction_ConcurrentBeginSerializes`, which
starts a second EF Core transaction while the first remains active and verifies
that both transactions persist after the first one commits.

### SAH pool capacity

The default OPFS SAH pool capacity is now 64 slots for both plain and encrypted
workers. WAL databases can use multiple slots (`.db`, `-wal`, `-shm`, and
temporary journal files), so the previous smaller pool was easy to exhaust in
real offline workloads.

### Optional scalar function fallbacks

The worker registers SQLite-compatible fallbacks for optional scalar functions
that richer native builds commonly expose but the bundled sqlite-wasm package
does not: `soundex(X)` and scalar `sha3(X[,SIZE])` for SHA3-224, SHA3-256,
SHA3-384, and SHA3-512. `sha3` returns a BLOB like SQLite's upstream extension,
so existing SQL such as `hex(sha3('abc'))` works the same way in the browser.

## Known Unsupported Or Limited Areas

| Area | Status | Workaround or limitation |
| --- | --- | --- |
| Synchronous ADO.NET execution (`ExecuteNonQuery`, `ExecuteScalar`, sync readers) | Unsupported. Browser JS interop and worker calls are async. | Use EF Core async APIs and ADO.NET async APIs. |
| Synchronous transaction `Commit()` / `Rollback()` | Unsupported and throws. | Use `CommitAsync()` / `RollbackAsync()`. Sync `Dispose()` queues rollback cleanup, but async disposal is preferred. |
| Synchronous transaction savepoints | Unsupported and throws. | Use `CreateSavepointAsync`, `RollbackToSavepointAsync`, and `ReleaseSavepointAsync` through EF Core, or `SaveAsync`, `RollbackAsync(savepointName)`, and `ReleaseAsync` on the ADO.NET transaction. |
| Multi-tab same-database writes | Limited by OPFS and SAH access-handle locking. | Keep one active app tab per origin/database for write workloads. Use app-level handoff or reload behavior for multiple tabs. |
| EF Core `DateTimeOffset` / `TimeSpan` member and method translations | Limited by the upstream EF Core SQLite provider rather than by SqliteWasmBlazor. These types are type-mapped and round-trip as SQLite TEXT, and `ToString()` conversion is covered, but provider-level member translations are not equivalent to `DateTime`, `DateOnly`, or `TimeOnly`. | Use `DateTime`, `DateOnly`, or `TimeOnly` columns for queryable date/time parts, project to client code after filtering, or use raw SQL over SQLite date/time functions when you need a specific server-side expression. |
| Native SQLite loadable extensions | Unsupported in WebAssembly package form. | Bundle needed functionality into sqlite-wasm or implement SQL functions in the worker. |
| SQLite optional compile-time extensions not bundled in sqlite-wasm, such as `geopoly`, `zipfile`, `completion`, `sha3_query`, SHA3 aggregate/query helpers, or third-party extensions | Limited by the packaged wasm build. Scalar `soundex` and `sha3` are now supplied by worker fallbacks, but virtual-table modules and helpers that need deeper SQLite engine integration are still absent. | Check `sqlite_compileoption_used`, `pragma_function_list`, and `pragma_module_list`; add a worker function fallback for scalar functions or rebuild the wasm package with the needed option/module. |
| OS file paths and arbitrary file I/O | Unsupported by browser sandbox. | Use OPFS database names, import/export APIs, or browser file picker flows. |
| True native busy-handler/file-lock parity | Partial. The worker serializes logical requests, but browser OPFS locking is not identical to OS SQLite locking. | Prefer async transaction scopes and keep write transactions short. |
| Long-running sync blocking semantics | Browser limitation. Blocking the UI thread while waiting for worker I/O is not safe. | Keep database access async end to end. |

## Validation Commands

Provider/package build:

```bash
dotnet build src/Base/SqliteWasmBlazor/SqliteWasmBlazor.csproj -c Debug
dotnet build src/Crypto/SqliteWasmBlazor.Crypto/SqliteWasmBlazor.Crypto.csproj -c Debug
```

TypeScript worker bundles:

```bash
npm run build --prefix src/Base/SqliteWasmBlazor/TypeScript
npm run build --prefix src/Crypto/SqliteWasmBlazor.Crypto/TypeScript
```

Browser EF compatibility tests require the .NET `wasm-tools` workload:

```bash
dotnet workload restore
dotnet test tests/SqliteWasmBlazor.Tests/SqliteWasmBlazor.Tests.csproj
```

In sandboxed macOS environments, Playwright Chromium may fail before tests run
with `MachPortRendezvousServer` permission errors. Treat that as a local
browser-launch restriction; run the same command on an unsandboxed shell or CI
runner for the authoritative browser result.
