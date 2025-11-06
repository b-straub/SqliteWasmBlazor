# Race Condition Test Guide

## Overview

This document describes the race condition tests created to verify the OPFS async write coordination issue discovered in WebAppBase full sync operations.

## Problem Statement

### The Race Condition

**Symptom**: `SQLITE_CONSTRAINT_PRIMARYKEY: UNIQUE constraint failed: todoLists.id`

**Root Cause**: When performing DELETE followed immediately by INSERT with overlapping primary keys:
1. Database uses `PRAGMA journal_mode = WAL` (Write-Ahead Logging)
2. With WAL mode, SQLite's default `PRAGMA synchronous = NORMAL` does NOT call xSync() after implicit transactions
3. EF Core's `SaveChangesAsync()` uses implicit transactions (auto-wrap each SaveChanges)
4. Worker executes SQL synchronously, but xSync() is NOT called, so writes stay in WAL file
5. Next `SaveChangesAsync()` may read stale data from database before WAL checkpoint occurs
6. SQLite sees duplicate primary key → constraint violation

**Key Insight**: FileSystemSyncAccessHandle.flush() IS synchronous (not buffered), but SQLite WAL mode doesn't call it after implicit transactions!

**Context in WebAppBase**:
- `PurgeTableDataAsync()` deletes all entities from a table
- `LoadAsync()` immediately inserts new entities from server
- Some entities may have the same GUIDs as deleted entities
- Without proper coordination, UNIQUE constraint violations occur

## Test Cases

### Test 1: PurgeThenLoadRaceConditionTest

**File**: `TestInfrastructure/Tests/RaceConditions/PurgeThenLoadRaceConditionTest.cs`
**Test Name**: `RaceCondition_PurgeThenLoad`

**Purpose**: Reproduce the race condition to verify it exists.

**Scenario**:
1. Create 3 TodoLists with known GUIDs (11111..., 22222..., 33333...)
2. Delete all TodoLists using `RemoveRange()` + `SaveChangesAsync()`
3. Clear change tracker
4. Immediately insert 3 new TodoLists (2 with overlapping GUIDs, 1 new GUID)
5. Call `SaveChangesAsync()` again

**Expected Behavior WITHOUT Fix**:
- Test should FAIL with: `SQLITE_CONSTRAINT_PRIMARYKEY: UNIQUE constraint failed: todoLists.id`
- Error occurs during Step 4's `SaveChangesAsync()`

**Expected Behavior WITH Fix** (after implementing transaction-based solution):
- Test should PASS
- All operations complete successfully

### Test 2: PurgeThenLoadWithTransactionTest

**File**: `TestInfrastructure/Tests/RaceConditions/PurgeThenLoadWithTransactionTest.cs`
**Test Name**: `RaceCondition_PurgeThenLoadWithTransaction`

**Purpose**: Verify that using explicit transactions fixes the race condition.

**Scenario**:
1. Create 3 TodoLists with known GUIDs
2. **BEGIN TRANSACTION**
3. Delete all TodoLists using `RemoveRange()` + `SaveChangesAsync()`
4. Clear change tracker
5. Immediately insert 3 new TodoLists (2 with overlapping GUIDs, 1 new GUID)
6. Call `SaveChangesAsync()` again
7. **COMMIT TRANSACTION** ← Forces xSync() call to flush OPFS writes

**Expected Behavior**:
- Test should PASS
- Transaction commit ensures OPFS writes complete before proceeding
- No UNIQUE constraint violations occur

## Running the Tests

### Using the Test App UI

1. Build the test project:
   ```bash
   cd /Users/berni/Projects/SQLiteNET
   dotnet build SQLiteNET.Opfs.TestApp/SQLiteNET.Opfs.TestApp.csproj
   ```

2. Run the test app (requires browser):
   ```bash
   # Note: This requires explicit user permission per CLAUDE.md
   cd SQLiteNET.Opfs.TestApp
   dotnet run
   ```

3. Navigate to the test app in your browser
4. Look for "Race Conditions" category
5. Run `RaceCondition_PurgeThenLoad` first to verify the issue exists
6. Run `RaceCondition_PurgeThenLoadWithTransaction` to verify the fix

### Expected Test Results

#### Before Fix Implementation

```
Test: RaceCondition_PurgeThenLoad
Status: ❌ FAILED
Error: Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes
Inner: SqliteException: SQLite Error 19: 'UNIQUE constraint failed: todoLists.id'
```

```
Test: RaceCondition_PurgeThenLoadWithTransaction
Status: ✅ PASSED
Result: OK - Transaction-based race condition fix verified
```

#### After Fix Implementation (in WebAppBase)

Both tests should pass:

```
Test: RaceCondition_PurgeThenLoad
Status: ✅ PASSED
Result: OK - Race condition test completed successfully
```

```
Test: RaceCondition_PurgeThenLoadWithTransaction
Status: ✅ PASSED
Result: OK - Transaction-based race condition fix verified
```

## Understanding the Fix

### Why Transactions Solve the Problem

**Explicit transactions** (via `BeginTransactionAsync()` + `CommitAsync()`) trigger xSync(), unlike implicit transactions:

1. **Implicit Transaction** (`SaveChangesAsync()` without `BeginTransaction`):
   - With `synchronous=NORMAL` in WAL mode: NO xSync() called
   - Writes go to WAL file but may not be visible to next read
   - Can cause UNIQUE constraint violations with overlapping keys

2. **Explicit Transaction** (`BeginTransactionAsync()` + `CommitAsync()`):
   - ALWAYS calls xSync() on COMMIT (even with `synchronous=NORMAL`)
   - Forces `file.sah.flush()` which is synchronous (not buffered)
   - Ensures writes are visible to subsequent reads
   - Guarantees ACID durability

### Code Pattern (Before Fix)

```csharp
// ❌ PROBLEMATIC: No coordination between operations
public async Task PurgeTableDataAsync(string tableName)
{
    context.TodoLists.RemoveRange(context.TodoLists);
    await context.SaveChangesAsync(); // Returns before OPFS flush
    context.ChangeTracker.Clear();
}

public async Task LoadAsync()
{
    // May execute before previous DELETE is flushed to OPFS
    context.TodoLists.AddRange(newLists);
    await context.SaveChangesAsync(); // UNIQUE constraint violation!
}
```

### Code Pattern (After Fix)

```csharp
// ✅ FIXED: Transaction ensures coordination
public async Task FullSyncAsync()
{
    await using var transaction = await context.Database.BeginTransactionAsync();

    // Step 1: Purge
    context.TodoLists.RemoveRange(context.TodoLists);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Step 2: Load
    context.TodoLists.AddRange(newLists);
    await context.SaveChangesAsync();

    // Step 3: Commit - Forces xSync() and OPFS flush
    await transaction.CommitAsync();
}
```

## Test Architecture

### Test Infrastructure

All tests inherit from `SqliteWasmTest`:

```csharp
internal abstract class SqliteWasmTest(IDbContextFactory<TodoDbContext> factory)
{
    public abstract string Name { get; }
    protected IDbContextFactory<TodoDbContext> Factory { get; } = factory;
    public abstract ValueTask<string?> RunTestAsync();
}
```

### Key Features

1. **Isolation**: Each test uses `IDbContextFactory` to create fresh contexts
2. **Async/Await**: All operations use proper async patterns
3. **Verification**: Tests verify initial state, perform operations, and verify final state
4. **Error Propagation**: Exceptions thrown by tests are caught and displayed in UI

## Integration with WebAppBase

### Where to Apply the Fix

**File**: `tools/WebAppBase.CrudGenerator/Templates/DbContextOperationsTemplate.cs`

**Method**: `FullSyncAsync()` (generated code in `DbContextOperations.g.cs`)

**Change Required**:
1. Wrap `PurgeTableDataAsync()` + all `LoadAsync()` calls in a single transaction
2. Remove per-table `SaveChangesAsync()` calls after each `LoadAsync()`
3. Call `SaveChangesAsync()` once at the end, before `CommitAsync()`

**Pseudocode**:
```csharp
private async Task FullSyncAsync(Dictionary<string, FullSyncData> fullSyncData)
{
    await using var transaction = await _context.Database.BeginTransactionAsync();

    foreach (var (tableName, data) in fullSyncData)
    {
        await PurgeTableDataAsync(tableName);

        switch (tableName)
        {
            case "todoLists":
                await _todolistOperations.LoadAsync(_context);
                // NO SaveChangesAsync here!
                break;
            case "todos":
                await _todoOperations.LoadAsync(_context);
                // NO SaveChangesAsync here!
                break;
        }
    }

    // Single SaveChanges for all tables
    await _context.SaveChangesWithoutSyncAsync();

    // Commit transaction - Forces xSync() for all tables
    await transaction.CommitAsync();
}
```

## Verification Checklist

After implementing the fix in WebAppBase:

- [ ] Run `RaceCondition_PurgeThenLoad` in SQLiteNET test app → Should PASS
- [ ] Run `RaceCondition_PurgeThenLoadWithTransaction` → Should PASS
- [ ] Deploy WebAppBase with transaction-based fix
- [ ] Clear browser database (DevTools → Application → Storage → Clear site data)
- [ ] Login to WebAppBase (triggers initial full sync)
- [ ] Verify no UNIQUE constraint errors in console
- [ ] Create TodoList offline
- [ ] Go online and sync
- [ ] Verify no errors during delta sync
- [ ] Check database state: Verify both synced and offline data exist

## Known Limitations

### Transaction Isolation Level

By default, SQLite uses SERIALIZABLE isolation. This is appropriate for full sync operations but may impact concurrency if multiple operations run in parallel.

**Not a concern for WebAppBase** because:
- Full sync is a single-threaded operation
- User can't interact with UI during full sync
- Delta sync doesn't use transactions (no purge operation)

### Performance Impact

Transactions add minimal overhead:
- ~1-2ms per commit on modern hardware
- Necessary for ACID guarantees
- Already required for correctness, not an optimization tradeoff

## Debugging Tips

### Console Logs to Watch

```javascript
// Good signs:
"Purged all data from table: todoLists (within transaction)"
"Loaded new data immediately after purge (within transaction)"
"Transaction committed - OPFS writes guaranteed to be flushed"

// Bad signs (before fix):
"Purged all data from table: todoLists"
// Immediately followed by:
"SqliteException: SQLite Error 19: 'UNIQUE constraint failed: todoLists.id'"
```

### DevTools Inspection

1. Open DevTools → Application → Storage
2. Check IndexedDB → Look for OPFS entries
3. Monitor timing between DELETE and INSERT operations
4. Check SQLite database file size changes

### Test Isolation

Each test run should use a fresh database. If tests interfere with each other:

```bash
# Clear OPFS storage between test runs
# DevTools → Application → Storage → Clear site data
```

## References

- **SQLite Transaction Documentation**: https://www.sqlite.org/lang_transaction.html
- **OPFS SAHPool VFS**: https://sqlite.org/wasm/doc/trunk/persistence.md#sahpool
- **EF Core Transactions**: https://learn.microsoft.com/en-us/ef/core/saving/transactions
- **WebAppBase Issue**: `/Users/berni/Projects/SQLiteNET/WEBAPPBASE-SQLITE-MIGRATION-ISSUES.md`

---

**Document Version**: 1.0
**Created**: 2025-11-06
**Status**: Active - Tests implemented, awaiting verification
