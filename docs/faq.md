# FAQ

## How is this different from besql?

besql uses Cache Storage API to emulate a filesystem. SqliteWasmBlazor uses **real OPFS filesystem** with synchronous access, providing true native-like performance and the ability to run the actual .NET SQLite provider.

## Can I use this in production?

Yes! The technology is stable (OPFS is a W3C standard), and all major browsers support it. The library has been tested with complex real-world scenarios.

## What about mobile browsers?

Mobile Chrome (Android 108+) and Safari (iOS 16.4+) both support OPFS with synchronous access handles.

## How do I export/backup my database?

The database files are in OPFS at `/databases/YourDb.db`. SqliteWasmBlazor provides full export/import functionality via the `ISqliteWasmDatabaseService` interface. See the Demo app's TodoImportExport component for a complete implementation example with MessagePack serialization.

## Is this compatible with existing EF Core code?

Yes! All standard EF Core features work: migrations, relationships, LINQ queries, change tracking, etc.

## Why can't I open multiple browser tabs?

OPFS uses exclusive synchronous access handles - only one tab can hold a write lock on the database at a time. This is a browser API limitation, not a library limitation. Use the [Multi-View pattern](patterns.md#multi-view-instead-of-multi-tab) instead.

## What happens if I open another tab anyway?

The second tab will fail to acquire the database lock and show an error message. The first tab continues to work normally.

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

SqliteWasmBlazor uses the official sqlite-wasm build (currently 3.50.4) from the SQLite project.

## Can I use raw SQL?

Yes! You can use the ADO.NET provider directly for raw SQL queries. See [ADO.NET Usage](ado-net.md) for details.

## How do migrations work?

EF Core migrations work normally. The `InitializeSqliteWasmDatabaseAsync` extension method automatically applies pending migrations at startup with automatic migration history recovery. See [Advanced Features](advanced-features.md#migrations) for project structure recommendations.

## What's the performance like?

- **Initial Load**: ~100-200ms (worker initialization + OPFS setup)
- **Query Execution**: < 1ms for simple queries, 10-50ms for complex joins
- **Persistence**: Automatic after `SaveChanges()`, ~10-30ms overhead

See [Architecture](architecture.md#performance-characteristics) for more details.
