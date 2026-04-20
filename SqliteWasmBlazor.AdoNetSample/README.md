# SqliteWasmBlazor - ADO.NET Sample

This sample demonstrates using **SqliteWasmBlazor without Entity Framework Core**, using only raw ADO.NET APIs.

## What This Sample Shows

- ✅ Direct ADO.NET usage (`SqliteWasmConnection`, `SqliteWasmCommand`, `SqliteWasmDataReader`)
- ✅ No EF Core dependency
- ✅ Raw SQL queries with parameters
- ✅ CRUD operations (Create, Read, Delete)
- ✅ Persistent storage in OPFS (data survives browser restarts)
- ✅ Minimal setup - just initialize the worker bridge

## Key Files

### Program.cs
Shows how to initialize SqliteWasm without EF Core:

```csharp
// Initialize SqliteWasm for ADO.NET usage (no EF Core needed!)
// Pass HostEnvironment so sub-path deployments (e.g. <base href="/myapp/">) resolve the worker correctly.
await host.Services.InitializeSqliteWasmAsync(builder.HostEnvironment);
```

> **Sub-path deployments:** passing `builder.HostEnvironment` is the recommended approach. The library derives `baseHref` from `HostEnvironment.BaseAddress`, which already reflects the `<base href>` baked in at build time. See [Deploying Under a Sub-path](../docs/advanced-features.md#deploying-under-a-sub-path) for details.

### Pages/Home.razor
Complete ADO.NET example with:
- Creating database tables
- Executing parameterized queries
- Reading results with `DataReader`
- Proper connection lifecycle management

## Running the Sample

```bash
cd SqliteWasmBlazor.AdoNetSample
dotnet run
```

Then navigate to `https://localhost:5001` in your browser.

## Key Differences from EF Core

| Aspect | EF Core Sample | ADO.NET Sample (This) |
|--------|----------------|------------------------|
| Setup | `AddDbContextFactory<T>()` | `InitializeSqliteWasmAsync()` |
| Schema | Migrations | Raw SQL `CREATE TABLE` |
| Queries | LINQ | Raw SQL with parameters |
| Complexity | Higher | Lower |
| Control | Less | More |

## Browser Requirements

- Chrome 108+, Edge 108+, Firefox 111+, or Safari 16.4+
- OPFS (Origin Private File System) support required

## Learn More

See the main [README.md](../README.md) for complete documentation.
