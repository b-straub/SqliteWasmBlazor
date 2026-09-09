# FAQ

## How is this different from besql?

besql emulates a filesystem over the Cache Storage API. SqliteWasmBlazor runs
the real SQLite engine on real OPFS files with synchronous access handles — see
[Architecture](architecture.md).

## Can I use this in production?

Yes! The technology is stable (OPFS is a W3C standard), and all major browsers support it. The library has been tested with complex real-world scenarios.

## How do I export/backup my database?

```csharp
await DatabaseService.ExportDatabaseToDownloadAsync("TodoDb.db", "backup.db");
```

No extra package needed, and nothing holds the file in managed memory. The full
set — one database or many, in or out, to a `Stream` or a download — is in
[Moving Databases In and Out](advanced-features.md#moving-databases-in-and-out).

## Is this compatible with existing EF Core code?

Yes! All standard EF Core features work: migrations, relationships, LINQ queries, change tracking, etc.

## Why can't I open multiple browser tabs?

A synchronous access handle is exclusive, so only one tab can hold the database
open. The second tab fails to acquire the lock and says so; the first keeps
working. Put your views in one tab instead — see the
[Multi-View pattern](patterns.md#multi-view-instead-of-multi-tab).

## How large can the database be?

OPFS quota is typically several GB per origin, depending on available disk space and browser policies. The library has been tested with databases containing 100k+ records.

## Does it work offline?

Yes! Once the PWA is installed and the initial data is loaded, all database operations work completely offline. Data persists across browser restarts and app updates.

## Browser Support

| Browser | Version | OPFS Support |
|---------|---------|--------------|
| Chrome  | 108+    | Full SAH support |
| Edge    | 108+    | Full SAH support |
| Firefox | 111+    | Full SAH support |
| Safari  | 16.4+   | Full SAH support |

All modern browsers (2023+) support OPFS with Synchronous Access Handles, including mobile browsers (iOS/iPadOS Safari, Android Chrome).

## What SQLite version is used?

SqliteWasmBlazor uses the official sqlite-wasm build (currently 3.53.0) from the SQLite project.

## Can I use raw SQL?

Yes! You can use the ADO.NET provider directly for raw SQL queries. See [ADO.NET Usage](ado-net.md) for details.

## How do migrations work?

EF Core migrations work normally. `AddSqliteWasmDbContext<T>()` declares a context and `<SqliteWasmDatabaseInitializer/>` applies its pending migrations after the first render, with automatic migration history recovery. See [Advanced Features](advanced-features.md#migrations) for project structure recommendations.

## What's the performance like?

Sub-millisecond simple queries, tens of milliseconds for complex joins, and
persistence is part of `SaveChanges()`. Numbers in
[Architecture](architecture.md#performance-characteristics).
