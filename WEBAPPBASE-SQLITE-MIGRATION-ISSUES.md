# WebAppBase SQLite Migration - Testing & Issues

## Migration Status: ⚠️ Partially Complete

### ✅ What's Working

1. **Database Infrastructure**
   - ✅ SQLite WASM worker initialized successfully
   - ✅ OPFS SAHPool persistence active (`[SQLite Worker] Opened database: TodoDb.db with OPFS SAHPool`)
   - ✅ Database schema created with all tables (pendingChanges, todoLists, todos, settings)
   - ✅ Foreign keys and indexes created correctly
   - ✅ WAL mode enabled (`PRAGMA journal_mode = 'wal'`)

2. **Sync Architecture**
   - ✅ InitializeSyncAsync runs with new MAX(UpdatedAt) fallback logic
   - ✅ Full sync safety checks active (checks for pending changes and never-synced entities)
   - ✅ Initial sync completed successfully (loaded TodoList and Todos from server)
   - ✅ Console logs show: "Table todolists: Empty, using epoch for full sync" (correct for fresh install)

3. **Safety Features**
   - ✅ PurgeTableDataAsync checks for pending changes before purging
   - ✅ PurgeTableDataAsync checks for never-synced entities (SyncedAt = NULL)
   - ✅ Offline data protection logic in place

---

## ❌ Critical Issue: EF Core Query Translation Failure

### Error Details

```
System.InvalidOperationException: NoElements
   at System.Linq.Enumerable.Single[Boolean](IEnumerable`1 source)
   at System.Linq.Queryable.Any[TodoList](IQueryable`1 source)
   at WebAppBase.UserSample.Pages.TodoManager.<BuildRenderTree>b__65_6
   in /Users/berni/Projects/WebAppBase/WebAppBase.UserSample/Pages/TodoManager.razor:line 31
```

### Root Cause

**Location**: `TodoManager.razor:31`
```razor
@if (ActiveTodoLists.Any())
```

**Problem**: Synchronous `IQueryable<T>.Any()` in Razor markup doesn't work with SQLite provider.

**Why it worked with InMemory**:
- InMemory provider is lenient and executes queries synchronously in-memory
- SQLite provider is stricter and requires proper async/await patterns

**Why it fails with SQLite**:
- `ActiveTodoLists` is an `IQueryable<TodoList>` (deferred execution)
- Calling `.Any()` synchronously in Razor triggers query execution
- SQLite query provider generates SQL: `SELECT EXISTS (SELECT 1 FROM "todoLists" WHERE "isActive")`
- The query execution path hits `Single()` on an enumerable that expects results but gets none
- Error: `NoElements` exception thrown

### Code Context

**TodoManager.razor.cs (lines 44-47)**:
```csharp
private IQueryable<TodoList> ActiveTodoLists =>
    DbContext.TodoLists
        .Where(tl => tl.IsActive)
        .OrderBy(tl => tl.Title);
```

**TodoManager.razor (line 31)**:
```razor
@if (ActiveTodoLists.Any())
{
    <MudTable T="TodoList" Items="@(ActiveTodoLists.ToList())">
```

---

## 🔧 Solutions to Implement

### Option 1: Materialize Query Asynchronously (Recommended)

**Change in TodoManager.razor.cs**:
```csharp
private List<TodoList> _activeTodoLists = [];

protected override async Task OnInitializedAsync()
{
    await base.OnInitializedAsync();
    await LoadTodoListsAsync();
}

private async Task LoadTodoListsAsync()
{
    _activeTodoLists = await DbContext.TodoLists
        .Where(tl => tl.IsActive)
        .OrderBy(tl => tl.Title)
        .ToListAsync();
    StateHasChanged();
}
```

**Change in TodoManager.razor**:
```razor
@if (_activeTodoLists.Any())
{
    <MudTable T="TodoList" Items="@_activeTodoLists">
```

**Pros**:
- ✅ Proper async/await pattern
- ✅ Data materialized once, reused multiple times
- ✅ No query translation issues
- ✅ Better performance (no repeated queries)

**Cons**:
- ⚠️ Needs manual refresh after data changes
- ⚠️ More code to maintain

---

### Option 2: Use `AnyAsync()` with Conditional Rendering

**Change in TodoManager.razor.cs**:
```csharp
private bool _hasActiveLists;

protected override async Task OnInitializedAsync()
{
    await base.OnInitializedAsync();
    _hasActiveLists = await DbContext.TodoLists.AnyAsync(tl => tl.IsActive);
}
```

**Change in TodoManager.razor**:
```razor
@if (_hasActiveLists)
{
    <MudTable T="TodoList" Items="@(ActiveTodoLists.ToList())">
```

**Pros**:
- ✅ Keeps IQueryable for data binding
- ✅ Async check avoids query translation issues

**Cons**:
- ⚠️ `.ToList()` still executes synchronously (but works since we know data exists)
- ⚠️ Two separate queries (one for check, one for data)

---

### Option 3: Use `AsEnumerable()` Client-Side Evaluation

**Change in TodoManager.razor**:
```razor
@if (ActiveTodoLists.AsEnumerable().Any())
{
    <MudTable T="TodoList" Items="@(ActiveTodoLists.ToList())">
```

**Pros**:
- ✅ Minimal code changes
- ✅ Forces client-side evaluation

**Cons**:
- ❌ Still synchronous (blocks UI thread)
- ❌ Loads entire table into memory before filtering
- ❌ Not recommended for large datasets

---

## 📋 Testing Checklist

### Phase 1: Fix Query Translation Issues
- [ ] Implement Option 1 (recommended) or Option 2
- [ ] Test app loads without `NoElements` exception
- [ ] Verify TodoLists display correctly
- [ ] Verify Todos display correctly

### Phase 2: Basic CRUD Operations
- [ ] Create TodoList → Verify saved to database
- [ ] Create Todo → Verify saved to database
- [ ] Update Todo → Verify changes persist
- [ ] Delete Todo → Verify removed from database
- [ ] Refresh browser → Verify all data persists (OPFS working)

### Phase 3: Offline Data Protection
- [ ] Go offline (DevTools → Network → Offline)
- [ ] Create TodoList "Shopping"
- [ ] Create Todo "Buy milk"
- [ ] Observe database state: `SyncedAt = NULL` for new items
- [ ] Go online
- [ ] Observe console: Should show "Offline data detected, using MAX(UpdatedAt)"
- [ ] Verify sync uploads offline data successfully
- [ ] Verify `SyncedAt` populated after sync
- [ ] **Critical**: Verify no full sync purge occurred

### Phase 4: Full Sync Safety
- [ ] Clear browser database (Dev Tools → Application → Storage → Clear site data)
- [ ] Login and let initial full sync complete
- [ ] Create TodoList offline
- [ ] Attempt to trigger full sync (e.g., wait 30+ days or force via server)
- [ ] **Expected**: App should throw error: "Cannot perform full sync: X never-synced entities exist"
- [ ] Verify error prevents data loss

### Phase 5: Sync Edge Cases
- [ ] **Scenario**: User has synced data + creates new item offline
  - [ ] Verify `InitializeSyncAsync` uses `MAX(SyncedAt)` (not `epochTime`)
  - [ ] Verify new item uploads via PendingChanges
  - [ ] Verify delta sync (not full sync)

- [ ] **Scenario**: Multiple offline edits to same entity
  - [ ] Modify TodoList title 3 times offline
  - [ ] Verify only latest change in PendingChanges (deduplication works)
  - [ ] Go online → Verify single upload with latest state

- [ ] **Scenario**: Conflict resolution
  - [ ] User A modifies Todo "Buy milk" offline
  - [ ] User B modifies same Todo on server
  - [ ] User A goes online
  - [ ] Verify conflict detected and resolved correctly

### Phase 6: Query Compatibility Audit
Review all components for similar query translation issues:

- [ ] **TodoManager.razor**: `ActiveTodoLists.Any()` ← **FOUND, NEEDS FIX**
- [ ] **TodoManager.razor**: `OrderedTodos.ToList()` ← Check if causes issues
- [ ] Search codebase for: `IQueryable<T>.Any()` in Razor files
- [ ] Search codebase for: `IQueryable<T>.Count()` in Razor files
- [ ] Search codebase for: `IQueryable<T>.First()` in Razor files
- [ ] Search codebase for: `IQueryable<T>.Single()` in Razor files

**Search Commands**:
```bash
# Find synchronous LINQ in Razor files
cd /Users/berni/Projects/WebAppBase
rg -t razor '\.Any\(\)' --glob '*.razor'
rg -t razor '\.Count\(\)' --glob '*.razor'
rg -t razor '\.First\(\)' --glob '*.razor'
rg -t razor '\.Single\(\)' --glob '*.razor'
```

---

## ⚠️ Warnings to Address (Non-Critical)

### 1. EF Core Functions Warning
```
warn: A connection of an unexpected type (SqliteWasmConnection) is being used.
The SQL functions prefixed with 'ef_' could not be created automatically.
```

**Impact**: Low - Only affects advanced EF functions (likely not used)

**Solution**: Can be suppressed in DbContext configuration:
```csharp
protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    optionsBuilder.ConfigureWarnings(warnings =>
        warnings.Ignore(RelationalEventId.AmbientTransactionWarning)
                .Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
}
```

### 2. Missing Assets (Cosmetic)
```
Failed to load resource: favicon.png:1 (404)
Failed to load resource: icon-192.png:1 (404)
```

**Impact**: None - Cosmetic only

**Solution**: Add icons to `wwwroot/` directory (low priority)

### 3. Service Worker No-Op Warning
```
Fetch event handler is recognized as no-op.
```

**Impact**: None - Performance hint only

**Solution**: Remove no-op fetch handler from service worker (optional)

---

## 🎯 Next Steps

### Immediate (Blocking)
1. **Fix `ActiveTodoLists.Any()` query translation issue** (see Options 1-3 above)
2. **Test basic CRUD operations** work correctly
3. **Verify database persistence** across browser refresh

### High Priority
4. **Audit all Razor components** for synchronous IQueryable usage
5. **Test offline data protection** end-to-end
6. **Verify full sync safety checks** prevent data loss

### Medium Priority
7. **Test all sync edge cases** (conflicts, deduplication, etc.)
8. **Suppress EF functions warning** (optional)
9. **Performance testing** with larger datasets

### Low Priority
10. **Add missing icons** (cosmetic)
11. **Optimize service worker** (optional)

---

## 📝 Notes

### Database Location
- **Local**: `IndexedDB` → `OPFS` → `/TodoDb.db`
- **Browser**: Chrome DevTools → Application → Storage → IndexedDB → Check for OPFS entries
- **Clear**: DevTools → Application → Storage → "Clear site data"

### Console Logs to Watch
```javascript
// Good signs:
"[SQLite Worker] Opened database: TodoDb.db with OPFS SAHPool"
"Table todolists: Empty, using epoch for full sync"  // Fresh install
"Table todolists: Offline data detected, using MAX(UpdatedAt)"  // Has offline data
"Purged all data from table: todolists"  // Full sync executed

// Bad signs:
"Cannot perform full sync: X never-synced entities exist"  // Should NOT happen with offline data
"System.InvalidOperationException: NoElements"  // Query translation failure
```

### Key Files
- **Query Fix**: `/Users/berni/Projects/WebAppBase/WebAppBase.UserSample/Pages/TodoManager.razor.cs`
- **Sync Logic**: `/Users/berni/Projects/WebAppBase/tools/WebAppBase.CrudGenerator/Templates/DbContextOperationsTemplate.cs` (generator)
- **Generated Operations**: `/Users/berni/Projects/WebAppBase/WebAppBase.UserDB/obj/Debug/net10.0/generated/...` (output)
- **Database Extensions**: `/Users/berni/Projects/WebAppBase/WebAppBase.UserDBBase/Extensions/DbContextExtensions.cs`

---

## ✅ Success Criteria

The migration is complete when:

1. ✅ App loads without `NoElements` exception
2. ✅ All CRUD operations work correctly
3. ✅ Data persists across browser refresh
4. ✅ Offline data is protected from full sync purge
5. ✅ Sync uploads offline changes successfully
6. ✅ No unhandled exceptions in console
7. ✅ Performance is acceptable for typical usage

---

**Document Version**: 1.0
**Last Updated**: 2025-11-06
**Status**: Active - Critical issue identified, solutions provided