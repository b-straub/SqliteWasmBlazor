# EF Core Migrations with SQLite WASM - Complete Guide

## Overview
This project successfully implements EF Core migrations for SQLite in WebAssembly with OPFS persistence. All migrations work correctly without hanging or requiring special workarounds.

## Architecture

### Two-Project Structure
```
SqliteWasm.Data.Models/          # Class library for migrations
├── TodoDbContext.cs             # Shared DbContext definition
├── TodoDbContextFactory.cs      # Design-time factory using standard SQLite
├── Models/                      # Entity classes
│   ├── TodoItem.cs
│   ├── TypeTestEntity.cs
│   └── ...
└── Migrations/                  # Generated migration files
    ├── 20251106092921_InitialCreate.cs
    ├── 20251106092921_InitialCreate.Designer.cs
    └── TodoDbContextModelSnapshot.cs

SqliteWasm.Data/                 # Runtime WASM provider
├── SqliteWasmConnection.cs      # WASM connection via worker bridge
├── SqliteWasmHistoryRepository.cs  # Custom repo with disabled locking
└── SqliteWasmDbContextOptionsExtensions.cs
```

### Key Insight: Same DbContext, Different Providers
The **same DbContext** is used for both design-time and runtime:
- **Design-time**: `UseSqlite("Data Source=:memory:")` - Standard Microsoft.Data.Sqlite
- **Runtime**: `UseSqliteWasm(connection)` - Custom WASM with OPFS via worker bridge

## Design-Time: Generating Migrations

### Prerequisites
- `SqliteWasm.Data.Models` class library with EF Core packages
- `TodoDbContextFactory` implementing `IDesignTimeDbContextFactory<TodoDbContext>`

### Commands
```bash
# Generate a new migration
dotnet ef migrations add MigrationName --project SqliteWasm.Data.Models

# Remove the last migration
dotnet ef migrations remove --project SqliteWasm.Data.Models

# List all migrations
dotnet ef migrations list --project SqliteWasm.Data.Models
```

### TodoDbContextFactory Implementation
```csharp
public class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TodoDbContext>();

        // Use standard SQLite for design-time (no WASM, no worker, no browser)
        optionsBuilder.UseSqlite("Data Source=:memory:");

        return new TodoDbContext(optionsBuilder.Options);
    }
}
```

## Runtime: Applying Migrations

### Program.cs Configuration
```csharp
using SqliteWasm.Data.Models;
using System.Data.SQLite.Wasm;

// Configure DbContext with SqliteWasm provider
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db");
    options.UseSqliteWasm(connection);
});

var host = builder.Build();

// Initialize sqlite-wasm worker
await SqliteWasmWorkerBridge.Instance.InitializeAsync();

// Apply migrations at startup
using (var scope = host.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();

    // Option 1: Apply migrations (recommended for production)
    await dbContext.Database.MigrateAsync();

    // Option 2: Ensure database exists (simpler for development)
    // await dbContext.Database.EnsureCreatedAsync();
}

await host.RunAsync();
```

## Critical Fix: Migration Lock Bypass

### The Problem
EF Core 9.0 introduced a migration locking mechanism that uses an infinite polling loop. This causes `MigrateAsync()` to hang in WebAssembly because:

1. `SqliteHistoryRepository.AcquireDatabaseLockAsync()` contains `while (true)` loop
2. Loop polls every 1 second with `await Task.Delay(_retryDelay)`
3. The async polling doesn't work correctly in WASM environment
4. Result: Infinite hang after `INSERT OR IGNORE INTO "__EFMigrationsLock"`

### The Solution
Custom `SqliteWasmHistoryRepository` that bypasses locking:

```csharp
internal sealed class SqliteWasmHistoryRepository : SqliteHistoryRepository
{
    public SqliteWasmHistoryRepository(HistoryRepositoryDependencies dependencies)
        : base(dependencies)
    {
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
    {
        // Skip locking entirely - just return a no-op lock
        // Single-user browser environment doesn't need concurrent migration protection
        await Task.CompletedTask;
        return new NoOpMigrationsDatabaseLock(this);
    }

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        return new NoOpMigrationsDatabaseLock(this);
    }

    private sealed class NoOpMigrationsDatabaseLock : IMigrationsDatabaseLock
    {
        private readonly IHistoryRepository _historyRepository;

        public NoOpMigrationsDatabaseLock(IHistoryRepository historyRepository)
        {
            _historyRepository = historyRepository;
        }

        IHistoryRepository IMigrationsDatabaseLock.HistoryRepository => _historyRepository;

        public void ReacquireLock(bool connectionReopened) { }
        public Task ReacquireLockAsync(bool connectionReopened, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

### Service Registration
```csharp
public static DbContextOptionsBuilder UseSqliteWasm(
    this DbContextOptionsBuilder optionsBuilder,
    SqliteWasmConnection connection)
{
    optionsBuilder.UseSqlite(connection);
    optionsBuilder.ReplaceService<IRelationalDatabaseCreator, SqliteWasmDatabaseCreator>();

    // Critical: Replace history repository to disable migration locking
    optionsBuilder.ReplaceService<IHistoryRepository, SqliteWasmHistoryRepository>();

    return optionsBuilder;
}
```

## Migration Testing

### Test Infrastructure
All migration tests are located in `TestInfrastructure/Tests/Migrations/`:

1. **FreshDatabaseMigrateTest** - Verify MigrateAsync creates schema from scratch
2. **ExistingDatabaseMigrateIdempotentTest** - Verify MigrateAsync is idempotent
3. **MigrationHistoryTableTest** - Verify __EFMigrationsHistory table creation
4. **GetAppliedMigrationsTest** - Test GetAppliedMigrationsAsync/GetPendingMigrationsAsync
5. **DatabaseExistsCheckTest** - Test Database.CanConnectAsync behavior
6. **EnsureCreatedVsMigrateConflictTest** - Test mixing EnsureCreated and Migrate

### Running Tests
Navigate to `/TestRunner` page in the browser to run all tests automatically.

### Test Results
All 6 migration tests pass successfully:
- ✅ Migrations apply without hanging
- ✅ `__EFMigrationsHistory` table created and tracked
- ✅ Idempotent migration application
- ✅ Proper conflict handling when mixing methods

## Important Notes

### ✅ What Works
- Generate migrations using `dotnet ef migrations add`
- Apply migrations at runtime with `MigrateAsync()`
- All EF Core migration APIs work correctly
- OPFS persistence with synchronous access handles
- No special configuration needed in project files

### ❌ What's NOT Needed
- `<BlazorWebAssemblyLazyLoad Include="System.Private.Xml.wasm" />` - **NOT required**
- Custom XML serialization configuration
- Special migration command interceptors
- Modifying migration SQL generation

### ⚠️ Don't Mix Methods
Never mix `EnsureCreatedAsync()` and `MigrateAsync()`:
- `EnsureCreatedAsync()` creates tables but NOT `__EFMigrationsHistory`
- `MigrateAsync()` sees no history and tries to recreate tables
- Result: "table already exists" error

**Use one or the other consistently:**
- Production: Use `MigrateAsync()` for versioned schema management
- Development/Testing: Use `EnsureCreatedAsync()` for simpler setup

## Summary

### Architecture Decision: Separate Class Library
Creating `SqliteWasm.Data.Models` as a separate class library solves the Blazor WASM limitations:
- Standard .NET project structure (not browser-specific)
- EF Core tools can generate migrations
- Same DbContext used at both design-time and runtime
- Design-time factory uses standard SQLite
- Runtime uses SqliteWasm with OPFS

### Critical Fix: Custom History Repository
The custom `SqliteWasmHistoryRepository` is essential:
- Bypasses EF Core 9.0's infinite polling lock mechanism
- Returns no-op lock for single-user browser environment
- Registered via `ReplaceService<IHistoryRepository, ...>()`
- Enables `MigrateAsync()` to work without hanging

### Result
✅ Full EF Core migrations support in SQLite WASM with OPFS persistence
✅ No workarounds or special configuration needed
✅ All migration tests pass successfully
✅ Production-ready implementation
