# Known Issues with EF Core Migrations in SQLite WASM

## Migration Lock Hanging (EF Core 9.0+)

### Symptom
When calling `await context.Database.MigrateAsync()` in the browser, the operation hangs indefinitely after executing the `INSERT OR IGNORE INTO "__EFMigrationsLock"` command.

### Root Cause
EF Core 9.0 introduced a migration locking mechanism to prevent concurrent migrations. The implementation:

1. Creates a `__EFMigrationsLock` table with a timestamp column
2. Inserts a lock record: `INSERT OR IGNORE INTO "__EFMigrationsLock"("Id", "Timestamp") VALUES(1, '...');`
3. Executes `SELECT changes();` to check if the lock was acquired
4. Polls or waits for the lock to be released if another migration is running

The polling/waiting mechanism doesn't work correctly in the WebAssembly async environment, causing the operation to hang.

### Browser Console Log Example
```
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executing DbCommand [Parameters=[], CommandType='Text', CommandTimeout='30']
      INSERT OR IGNORE INTO "__EFMigrationsLock"("Id", "Timestamp") VALUES(1, '2025-11-06 10:13:42.49+00:00');
      SELECT changes();

info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (3ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
      INSERT OR IGNORE INTO "__EFMigrationsLock"("Id", "Timestamp") VALUES(1, '2025-11-06 10:13:42.49+00:00');
      SELECT changes();

dbug: RelationalEventId.CommandCreating[20103]
      Creating DbCommand for 'ExecuteScalar'.
      [HANGS HERE - never completes]
```

### Current Workaround
Use `EnsureCreatedAsync()` instead of `MigrateAsync()` for runtime schema creation:

```csharp
// In Program.cs
await dbContext.Database.EnsureDeletedAsync();
await dbContext.Database.EnsureCreatedAsync();  // Instead of MigrateAsync()
```

### Impact on Migration Tests
All 6 migration tests will fail or hang because they rely on `MigrateAsync()`:
- ✅ Tests compile successfully
- ❌ Tests hang at runtime when calling `MigrateAsync()`

Test files affected:
- `FreshDatabaseMigrateTest.cs`
- `ExistingDatabaseMigrateIdempotentTest.cs`
- `MigrationHistoryTableTest.cs`
- `GetAppliedMigrationsTest.cs`
- `DatabaseExistsCheckTest.cs`
- `EnsureCreatedVsMigrateConflictTest.cs`

### Design-Time Tooling (Unaffected)
The migration generation tooling works correctly because it uses standard Microsoft.Data.Sqlite:

```bash
# This works fine - generates migrations
dotnet ef migrations add InitialCreate --project SqliteWasm.Data.Models

# This also works - removes last migration
dotnet ef migrations remove --project SqliteWasm.Data.Models
```

### Potential Solutions

#### Option 1: Override Migration Locking (Best)
Create a custom `SqliteWasmMigrationsHistoryRepository` that disables locking:

```csharp
internal class SqliteWasmMigrationsHistoryRepository : SqliteHistoryRepository
{
    // Override to disable locking mechanism
    protected override bool SupportsConcurrencyControl => false;
}
```

Register in `SqliteWasmOptionsExtension`:

```csharp
protected override void ApplyServices(IServiceCollection services)
{
    services.TryAddScoped<IHistoryRepository, SqliteWasmMigrationsHistoryRepository>();
}
```

#### Option 2: Custom Migration Command Interceptor
Intercept and modify the locking SQL commands to be no-ops.

#### Option 3: Wait for EF Core Fix
Monitor https://github.com/dotnet/efcore for WASM-related migration improvements.

### Recommendation
Implement Option 1 (custom history repository) to disable the locking mechanism, as:
- Single-user browser environment doesn't need concurrent migration protection
- OPFS access is already single-threaded via Web Worker
- Minimal code changes required
- Follows EF Core extensibility patterns

### Related Files
- `/Users/berni/Projects/SQLiteNET/SqliteWasm.Data/SqliteWasmDatabaseCreator.cs`
- `/Users/berni/Projects/SQLiteNET/SqliteWasm.Data/SqliteWasmWorkerBridge.cs`
- `/Users/berni/Projects/SQLiteNET/SqliteWasm.Data/TypeScript/sqlite-worker.ts`
- `/Users/berni/Projects/SQLiteNET/SQLiteNET.Opfs.TestApp/Program.cs`

### Status
- **Investigation**: ✅ Complete
- **Permanent Fix**: ✅ **FULLY IMPLEMENTED** (Option 1 - Custom history repository)

### Implementation (2025-11-06)

#### Solution: Custom History Repository
Created `SqliteWasmHistoryRepository` that overrides `AcquireDatabaseLockAsync()` and `AcquireDatabaseLock()` to return a no-op lock instead of entering the infinite polling loop.

**Key Implementation Details:**
```csharp
// SqliteWasm.Data/SqliteWasmHistoryRepository.cs
internal sealed class SqliteWasmHistoryRepository : SqliteHistoryRepository
{
    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
    {
        // Skip locking entirely - return no-op lock
        // Single-user browser environment doesn't need concurrent migration protection
        await Task.CompletedTask;
        return new NoOpMigrationsDatabaseLock(this);
    }
}
```

**Service Registration:**
```csharp
// SqliteWasm.Data/SqliteWasmDbContextOptionsExtensions.cs
optionsBuilder.ReplaceService<IHistoryRepository, SqliteWasmHistoryRepository>();
```

**Files Modified:**
1. `SqliteWasm.Data/SqliteWasmHistoryRepository.cs` - NEW: Custom history repository with disabled locking
2. `SqliteWasm.Data/SqliteWasmDbContextOptionsExtensions.cs` - Added service replacement
3. `SQLiteNET.Opfs.TestApp/Program.cs` - Uses `MigrateAsync()` instead of `EnsureCreatedAsync()`
4. `TestInfrastructure/Tests/Migrations/FreshDatabaseMigrateTest.cs` - Fixed history table check
5. `TestInfrastructure/Tests/Migrations/EnsureCreatedVsMigrateConflictTest.cs` - Fixed context tracking issue

**Important: No XML Lazy Loading Required**
- ❌ `<BlazorWebAssemblyLazyLoad Include="System.Private.Xml.wasm" />` is **NOT needed**
- ✅ Migrations work without XML serialization library
- ✅ The custom history repository bypasses the locking mechanism that required XML

**Result:**
- ✅ `MigrateAsync()` works without hanging
- ✅ Migrations apply correctly at runtime in browser
- ✅ All 6 migration tests pass successfully
- ✅ No infinite polling loops
- ✅ `__EFMigrationsHistory` table created and tracked correctly
- ✅ Design-time tooling (`dotnet ef migrations add/remove`) works perfectly
