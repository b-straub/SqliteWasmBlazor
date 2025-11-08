# EF Core Migrations Testing in WASM/OPFS

This document describes the migration testing infrastructure for SQLite WASM with OPFS persistence.

## Overview

EF Core migrations work in the browser with our SQLite WASM implementation, allowing you to:
- Create migrations at design-time using `dotnet ef migrations add`
- Apply migrations at runtime in the browser using `Database.MigrateAsync()`
- Track applied migrations in the `__EFMigrationsHistory` table
- Persist migration state in OPFS across page refreshes

## Test Coverage

The migration tests verify the following scenarios:

### 1. Fresh Database Migration (`FreshDatabaseMigrateTest`)
**Test**: `Migration_FreshDatabaseMigrate`

Verifies that `MigrateAsync()` can create a database from scratch:
- Deletes existing database
- Calls `MigrateAsync()` on empty database
- Verifies schema is created
- Verifies `__EFMigrationsHistory` table exists
- Tests data insertion after migration

### 2. Idempotent Migrations (`ExistingDatabaseMigrateIdempotentTest`)
**Test**: `Migration_ExistingDatabaseIdempotent`

Verifies that `MigrateAsync()` is safe to call multiple times:
- Creates database with data
- Calls `MigrateAsync()` - should be no-op
- Verifies data is preserved
- Calls `MigrateAsync()` again
- Verifies data count unchanged

### 3. Migration History Tracking (`MigrationHistoryTableTest`)
**Test**: `Migration_HistoryTableTracking`

Verifies `__EFMigrationsHistory` table functionality:
- Checks table exists after migration
- Queries applied migrations
- Verifies no duplicate entries on repeated calls
- Logs migration IDs and EF Core versions

### 4. Applied Migrations Query (`GetAppliedMigrationsTest`)
**Test**: `Migration_GetAppliedMigrations`

Verifies EF Core's migration query APIs:
- `GetAppliedMigrationsAsync()` returns applied migrations
- `GetPendingMigrationsAsync()` returns empty list when up-to-date
- No duplicates after repeated `MigrateAsync()` calls

### 5. Database Existence Checks (`DatabaseExistsCheckTest`)
**Test**: `Migration_DatabaseExistsCheck`

Verifies database state detection:
- `CanConnectAsync()` returns false for non-existent database
- `CanConnectAsync()` returns true after creation
- Works correctly with both `MigrateAsync()` and `EnsureCreatedAsync()`

### 6. EnsureCreated vs Migrate Conflict (`EnsureCreatedVsMigrateConflictTest`)
**Test**: `Migration_EnsureCreatedVsMigrateConflict`

Verifies behavior when mixing database creation methods:
- **Scenario 1**: `EnsureCreatedAsync()` first, then `MigrateAsync()` - data preserved
- **Scenario 2**: `EnsureCreatedAsync()` after database exists - returns false, no-op
- **Scenario 3**: `MigrateAsync()` first, then `EnsureCreatedAsync()` - data preserved

## Creating Actual Migrations

Currently, the tests use `EnsureCreatedAsync()` which creates the schema without migrations. To use real migrations:

### Step 1: Create Initial Migration (Design-Time)

From the project root:

```bash
# Create initial migration
dotnet ef migrations add InitialCreate --project SQLiteNET.Opfs.TestApp

# This generates files in SQLiteNET.Opfs.TestApp/Migrations/:
# - 20250106123456_InitialCreate.cs (Up/Down methods)
# - 20250106123456_InitialCreate.Designer.cs (metadata)
# - TodoDbContextModelSnapshot.cs (current model state)
```

### Step 2: Apply Migration at Runtime (Browser)

Update `Program.cs` to use migrations instead of `EnsureCreated()`:

```csharp
// OLD: EnsureCreated approach (no migrations)
await dbContext.Database.EnsureCreatedAsync();

// NEW: Migration approach
await dbContext.Database.MigrateAsync();
```

### Step 3: Add New Migration (Schema Change)

1. Modify your entities (e.g., add a property):

```csharp
public class TodoItem
{
    // ... existing properties ...

    public int Priority { get; set; }  // New property
}
```

2. Create migration:

```bash
dotnet ef migrations add AddPriorityToTodoItem --project SQLiteNET.Opfs.TestApp
```

3. Next time the app runs, `MigrateAsync()` will apply only the new migration.

### Step 4: Verify in Browser

After running the app:

1. Open browser console
2. Check for migration logs
3. Query the database:

```javascript
// In browser console (if you have DB access exposed)
await context.Database.GetAppliedMigrationsAsync()
// Should show: ["20250106123456_InitialCreate", "20250106234567_AddPriorityToTodoItem"]
```

## Migration Persistence in OPFS

Migrations persist across browser sessions:

1. **First Run**: `MigrateAsync()` creates schema and `__EFMigrationsHistory`
2. **Page Refresh**: OPFS retains database file
3. **Subsequent Runs**: `MigrateAsync()` checks history, applies only new migrations

## Key Points

### DO ✅
- Use `MigrateAsync()` in production for version-controlled schema changes
- Run migrations at app startup
- Keep migration files in source control
- Test migrations in the browser before deploying

### DON'T ❌
- Mix `EnsureCreated()` and `MigrateAsync()` in production (pick one)
- Delete migration files manually (use `dotnet ef migrations remove`)
- Skip migrations in the middle of the history
- Edit applied migrations (create new ones instead)

## Differences from Server-Side EF Core

### Works the Same ✅
- `dotnet ef migrations add/remove/list`
- `Database.MigrateAsync()`
- `Database.GetAppliedMigrationsAsync()`
- `Database.GetPendingMigrationsAsync()`
- Migration Up/Down methods
- `__EFMigrationsHistory` tracking

### Limitations ⚠️
- No design-time database connection (migrations generated from model only)
- No `dotnet ef database update` (must run in browser)
- No direct SQL script generation for review
- OPFS persistence is browser-local (no cloud sync)

## Running the Tests

The migration tests are integrated into the test runner:

1. Navigate to `/TestRunner` page
2. Tests run automatically on page load
3. Look for "Migrations" category tests
4. All 6 tests should pass

## Troubleshooting

### Test Fails: "__EFMigrationsHistory table does not exist"
- You're using `EnsureCreatedAsync()` instead of `MigrateAsync()`
- Solution: Create migrations and switch to `MigrateAsync()`

### Test Fails: "Migration count changed"
- Duplicate migration applied
- Solution: Check for race conditions in test setup

### Test Fails: "Data lost after MigrateAsync"
- Migration incorrectly drops/recreates tables
- Solution: Review migration Up/Down methods

### Browser Console: "No migrations found"
- No migration files exist yet
- Solution: Run `dotnet ef migrations add InitialCreate`

## Next Steps

1. **Create First Migration**: Run `dotnet ef migrations add InitialCreate`
2. **Switch Program.cs**: Change `EnsureCreatedAsync()` to `MigrateAsync()`
3. **Run Tests**: Verify all migration tests pass
4. **Add Schema Change**: Test adding a new migration
5. **Verify Persistence**: Refresh browser, check migrations still applied
