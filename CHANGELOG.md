# Changelog

All notable changes to SqliteWasmBlazor are documented in this file.

## Version 0.7.2-pre

### Breaking Change: Stable Public API

`SqliteWasmWorkerBridge` is now `internal`. Database management operations are exposed through the new `ISqliteWasmDatabaseService` interface via dependency injection.

**Migration steps:**

1. Add service registration in `Program.cs`:
   ```csharp
   builder.Services.AddSqliteWasm();
   ```

2. Inject the interface in components:
   ```csharp
   @inject ISqliteWasmDatabaseService DatabaseService
   ```

3. Replace direct calls:
   ```csharp
   // Before
   await SqliteWasmWorkerBridge.Instance.DeleteDatabaseAsync("MyDb.db");

   // After
   await DatabaseService.DeleteDatabaseAsync("MyDb.db");
   ```

**Available methods on `ISqliteWasmDatabaseService`:**
- `ExistsDatabaseAsync(string databaseName)` - Check if database exists in OPFS
- `DeleteDatabaseAsync(string databaseName)` - Delete database from OPFS
- `RenameDatabaseAsync(string oldName, string newName)` - Rename database (atomic)
- `CloseDatabaseAsync(string databaseName)` - Close database connection in worker

This change encapsulates internal implementation details and provides a stable API surface for future versions.

---

## Incremental Database Export/Import (Delta Sync)

File-based incremental export/import for large databases in offline-first PWAs. Export only changed items since last checkpoint, transfer the file manually (USB, cloud storage, etc.), and import with conflict resolution:

```csharp
// Export only changes since last checkpoint (delta export)
<MessagePackFileDownload T="TodoItemDto"
    GetPageAsync="@GetDeltaTodoItemsPageAsync"  // Only items modified since checkpoint
    GetTotalCountAsync="@GetDeltaCountAsync"
    FileName="@($"delta-{DateTime.Now:yyyyMMdd}.msgpack")"
    Mode="ExportMode.Delta" />  // Delta mode includes UpdatedAt/DeletedAt ranges

// Import with conflict resolution strategy
<MessagePackFileUpload T="TodoItemDto"
    OnBulkInsertAsync="@DeltaMergeTodoItemsAsync"  // Smart merge instead of replace
    Mode="ImportMode.Delta"
    ConflictResolution="ConflictResolutionStrategy.LastWriteWins" />  // Or LocalWins/DeltaWins
```

### Key Features

**Automatic Checkpoint Management**
- Auto checkpoints created after every import/export operation
- Manual checkpoints with tombstone cleanup
- Checkpoint history with timestamp, description, and item counts
- Restore to any checkpoint with optional delta reapply

**Efficient Delta Tracking**
- Only exports items modified since last checkpoint (`UpdatedAt > lastCheckpointTime`)
- Includes soft-deleted items (tombstones) for proper sync
- Pending delta count shows items awaiting export
- Significantly reduces data transfer for large databases

**Three Conflict Resolution Strategies**
- **LastWriteWins** (default): Most recent `UpdatedAt` timestamp wins
- **LocalWins**: Local changes always preserved, imports only add new items
- **DeltaWins**: Imported changes always win, local items overwritten

**Soft Delete (Tombstones)**
- Items marked with `IsDeleted` flag instead of hard deletion
- `DeletedAt` timestamp tracks deletion time for delta sync
- Tombstones included in delta export for proper deletion propagation
- Manual tombstone cleanup before creating manual checkpoints

### Architecture

```
Database Timeline:
├─ Checkpoint 1 (Manual)     ← Baseline: 100 active items, 0 tombstones
│  └─ Created 10 items       ← UpdatedAt = 2025-11-17 10:00
│  └─ Deleted 2 items        ← DeletedAt = 2025-11-17 10:05
├─ Delta Export              ← Exports 12 items (10 new + 2 deleted)
├─ Checkpoint 2 (Auto)       ← Auto checkpoint: 108 active, 2 tombstones
│  └─ Import 5 items         ← Conflict resolution applied
├─ Checkpoint 3 (Auto)       ← Auto checkpoint after import
│  └─ Created 3 items        ← UpdatedAt = 2025-11-17 10:30
├─ Pending Delta: 3 items    ← Awaiting next export
```

### Conflict Resolution Examples

```csharp
// LastWriteWins: Compare timestamps
Local:    UpdatedAt = 2025-11-17 10:00, Title = "Local Edit"
Imported: UpdatedAt = 2025-11-17 10:05, Title = "Remote Edit"
Result:   Title = "Remote Edit" (newer timestamp wins)

// LocalWins: Keep local changes
Local:    Title = "My Local Changes"
Imported: Title = "Remote Changes"
Result:   Title = "My Local Changes" (local always wins)

// DeltaWins: Always accept imported
Local:    Title = "Local Changes", UpdatedAt = 2025-11-17 10:05
Imported: Title = "Remote Changes", UpdatedAt = 2025-11-17 09:00 (older!)
Result:   Title = "Remote Changes" (delta wins despite older timestamp)
```

### Database Schema Requirements

```csharp
public class TodoItem
{
    public Guid Id { get; set; }
    public string Title { get; set; }
    public DateTime UpdatedAt { get; set; }        // Required for delta sync
    public bool IsDeleted { get; set; }            // Soft delete flag
    public DateTime? DeletedAt { get; set; }       // Deletion timestamp
}

public class SyncState  // Checkpoint tracking
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }        // Checkpoint timestamp
    public string Description { get; set; }
    public int ActiveItemCount { get; set; }
    public int TombstoneCount { get; set; }
    public string CheckpointType { get; set; }     // "Auto" or "Manual"
}
```

### Implementation Pattern

```csharp
// Delta export query
private async Task<(List<TodoItemDto> Items, int TotalCount)> GetDeltaTodoItemsPageAsync(
    int skip, int take)
{
    await using var context = await DbContextFactory.CreateDbContextAsync();

    // Get last checkpoint timestamp
    var lastCheckpoint = await context.SyncState
        .OrderByDescending(s => s.CreatedAt)
        .FirstOrDefaultAsync();

    var lastCheckpointTime = lastCheckpoint?.CreatedAt ?? DateTime.MinValue;

    // Query items modified since checkpoint (including soft-deleted)
    var query = context.TodoItems
        .Where(t =>
            (t.UpdatedAt > lastCheckpointTime && !t.IsDeleted) ||  // Modified items
            (t.IsDeleted && t.DeletedAt.HasValue && t.DeletedAt.Value > lastCheckpointTime))  // Deletions
        .OrderBy(t => t.UpdatedAt);

    var totalCount = await query.CountAsync();
    var items = await query
        .Skip(skip)
        .Take(take)
        .Select(t => t.ToDto())
        .ToListAsync();

    return (items, totalCount);
}

// Delta import with conflict resolution
private async Task DeltaMergeTodoItemsAsync(List<TodoItemDto> dtos)
{
    await using var context = await DbContextFactory.CreateDbContextAsync();

    foreach (var dto in dtos)
    {
        var existingItem = await context.TodoItems
            .FirstOrDefaultAsync(t => t.Id == dto.Id);

        if (existingItem is not null)
        {
            // Apply conflict resolution strategy
            var shouldUpdate = _conflictResolution switch
            {
                ConflictResolutionStrategy.LastWriteWins => dto.UpdatedAt > existingItem.UpdatedAt,
                ConflictResolutionStrategy.LocalWins => false,  // Never update
                ConflictResolutionStrategy.DeltaWins => true,   // Always update
                _ => throw new InvalidOperationException($"Unknown strategy: {_conflictResolution}")
            };

            if (shouldUpdate)
            {
                // Update existing item
                existingItem.Title = dto.Title;
                existingItem.UpdatedAt = dto.UpdatedAt;
                existingItem.IsDeleted = dto.IsDeleted;
                existingItem.DeletedAt = dto.DeletedAt;
            }
        }
        else
        {
            // Add new item
            context.TodoItems.Add(dto.ToEntity());
        }
    }

    await context.SaveChangesAsync();

    // Create auto checkpoint after import
    await context.CreateCheckpointAsync(
        $"Auto checkpoint after delta import ({dtos.Count} items)",
        "Auto");
}

// Checkpoint creation extension method
public static async Task<SyncState> CreateCheckpointAsync(
    this TodoDbContext context,
    string description,
    string checkpointType = "Auto",
    CancellationToken cancellationToken = default)
{
    var activeCount = await context.TodoItems
        .CountAsync(t => !t.IsDeleted, cancellationToken);

    var tombstoneCount = await context.TodoItems
        .CountAsync(t => t.IsDeleted, cancellationToken);

    var checkpoint = new SyncState
    {
        CreatedAt = DateTime.UtcNow,
        Description = description,
        ActiveItemCount = activeCount,
        TombstoneCount = tombstoneCount,
        CheckpointType = checkpointType
    };

    context.SyncState.Add(checkpoint);
    await context.SaveChangesAsync(cancellationToken);

    return checkpoint;
}
```

### What This Is

A file-based incremental backup/restore system for large databases. Useful when you need to:
- Transfer only changes between devices (vs. transferring entire database)
- Keep incremental backups with restore points
- Reduce file transfer size for large databases (100k+ records)
- Handle conflicts when merging changes from different sources

### What This Is NOT

This is **not** a real-time sync solution. It requires:
- Manual file transfer (download delta → copy file → upload delta on other device)
- No automatic sync between devices/users

For real-time/automatic sync, see the [Datasync TodoApp](https://github.com/b-straub/Datasync/tree/main/samples/todoapp-blazor-wasm-offline) sample which demonstrates proper offline-first synchronization patterns.

### Use Cases

- **Offline-First PWAs**: Export changes before going offline, import when back online
- **Multi-Device Transfer**: Manually share database state via file transfer
- **Incremental Backups**: Keep checkpoint history with smaller backup files
- **Data Migration**: Move data between environments with conflict handling

### Best Practices

1. Always store timestamps in UTC (`DateTime.UtcNow`)
2. Display timestamps in local time (`ToLocalTime()`)
3. Set `UpdatedAt` on every entity modification
4. Use soft delete for entities that need sync
5. Clean tombstones before manual checkpoints
6. Choose conflict resolution strategy based on use case:
   - **LastWriteWins**: Most recent edit wins (general purpose)
   - **LocalWins**: User's local edits are sacred (offline-first apps)
   - **DeltaWins**: Server/remote is source of truth (cloud sync)

### Future Direction

This foundation could be extended toward decentralized sync solutions, but currently it's a building block for offline-first scenarios, not a complete sync system.

See the Demo app's Administration and TodoImportExport components for complete implementation examples.

---

## Database Import/Export

Export and import your entire database with schema validation and efficient binary serialization:

```csharp
// Export database to MessagePack file
<MessagePackFileDownload T="TodoItemDto"
    GetPageAsync="@GetTodoItemsPageAsync"
    GetTotalCountAsync="@GetTodoItemCountAsync"
    FileName="@($"backup-{DateTime.Now:yyyyMMdd}.msgpack")"
    SchemaVersion="1.0"
    AppIdentifier="MyApp" />

// Import database with validation
<MessagePackFileUpload T="TodoItemDto"
    OnBulkInsertAsync="@BulkInsertTodoItemsAsync"
    ExpectedSchemaVersion="1.0"
    ExpectedAppIdentifier="MyApp" />
```

### Features

- **Schema Validation** - Prevents importing incompatible data with version and app identifier checks
- **Efficient Serialization** - MessagePack binary format (60% smaller than JSON)
- **Streaming Export** - Handles large datasets with pagination (tested with 100k+ records)
- **Bulk Import** - Optimized SQL batching respects SQLite's 999 parameter limit
- **Progress Tracking** - Real-time progress updates during import/export operations
- **Type Safety** - Full DTO validation ensures data integrity

Perfect for:
- Database backups and restores
- Data migration between environments
- Sharing datasets between users
- Offline-first PWA scenarios

### How it works

Export streams data in MessagePack format with a file header (magic number "SWBMP", schema version, type info, record count) followed by serialized items. Import deserializes the stream in batches, validates the header, and uses raw SQL INSERT statements to preserve entity IDs while respecting SQLite's 999 parameter limit (166 rows per batch for 6-column entities). The header-first approach ensures schema compatibility before processing begins, preventing partial imports of incompatible data.

### Why sqlite-wasm needed patching

The official sqlite-wasm OPFS SAHPool VFS lacked a `renameFile()` implementation. The patch (`patches/@sqlite.org+sqlite-wasm+3.50.4-build1.patch`) adds this method to enable efficient database renaming by updating the SAH (Synchronous Access Handle) metadata mapping with the new path while keeping the physical file intact - avoiding expensive file copying for large databases.

See the Demo app's TodoImportExport component for a complete implementation example.

---

## Version 0.6.7-pre (2025-11-14)

### Log Level Configuration Change

The `SqliteWasmConnection` constructor now uses the standard `Microsoft.Extensions.Logging.LogLevel` enum instead of the custom `SqliteWasmLogLevel`:

```csharp
// Old (0.6.6-pre and earlier)
var connection = new SqliteWasmConnection("Data Source=MyDb.db", SqliteWasmLogLevel.Warning);

// New (0.6.7-pre and later)
using Microsoft.Extensions.Logging; // Add this using

// Default is LogLevel.Warning, so you can omit it:
var connection = new SqliteWasmConnection("Data Source=MyDb.db");

// Or specify a different level:
var connection = new SqliteWasmConnection("Data Source=MyDb.db", LogLevel.Error);
```

**Migration:** Simply add `using Microsoft.Extensions.Logging;` and change `SqliteWasmLogLevel` to `LogLevel`. If you were using the default `Warning` level, you can omit the parameter entirely.

Available log levels: `Trace`, `Debug`, `Information`, `Warning` (default), `Error`, `Critical`, `None`
