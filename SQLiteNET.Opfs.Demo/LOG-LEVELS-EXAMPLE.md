# OPFS Log Level Configuration

The OPFS storage service supports configurable log levels similar to MSBuild verbosity.

## Log Levels

| Level | Value | Output |
|-------|-------|--------|
| `None` | 0 | No logging |
| `Error` | 1 | Only errors |
| `Warning` | 2 | Errors and warnings (**default**) |
| `Info` | 3 | Errors, warnings, and informational messages |
| `Debug` | 4 | Everything including debug details |

## Configuration

### Option 1: During Registration (Recommended)

**Program.cs**:
```csharp
using SQLiteNET.Opfs.Abstractions;

// Minimal output (default)
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(options =>
{
    options.UseSqlite("Data Source=TodoDb.db");
});
// LogLevel: Warning (default) - only errors and warnings

// Info level - see successful operations
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(
    options => options.UseSqlite("Data Source=TodoDb.db"),
    opfsLogLevel: OpfsLogLevel.Info);

// Debug level - see all operations
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(
    options => options.UseSqlite("Data Source=TodoDb.db"),
    opfsLogLevel: OpfsLogLevel.Debug);

// No logging
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(
    options => options.UseSqlite("Data Source=TodoDb.db"),
    opfsLogLevel: OpfsLogLevel.None);
```

### Option 2: Runtime Configuration

Change log level at runtime by accessing the service:

```csharp
@inject IOpfsStorage OpfsStorage

protected override async Task OnInitializedAsync()
{
    // Change to Debug for troubleshooting
    OpfsStorage.LogLevel = OpfsLogLevel.Debug;

    await OpfsStorage.InitializeAsync();

    // Change back to Warning for normal operation
    OpfsStorage.LogLevel = OpfsLogLevel.Warning;
}
```

## Example Output by Level

### None
```
(no output)
```

### Error
```
[OpfsStorageService] ❌ Initialization failed: Worker creation failed
```

### Warning (Default)
```
[OpfsStorageService] ⚠ JSImport init failed: Method not found
[OpfsStorageService] ⚠ VFS tracking init failed (rc=1), using full sync
[OpfsStorageService] ⚠ Failed to get dirty pages (rc=1), falling back to full sync
```

### Info
```
[OpfsStorageService] ✓ OPFS initialized: OPFS Worker initialized successfully
[OpfsStorageService] ✓ Capacity: 10, Files: 0
[OpfsStorageService] ✓ JSImport interop initialized
[OpfsStorageService] ✓ VFS tracking initialized (page size: 4096 bytes)
[OpfsStorageService] ✓ Persisted 2 pages (8 KB)
```

### Debug
```
[OpfsStorageService] Starting initialization...
[OpfsStorageService] Initialize result: Success=True, Message=Initialized
[OpfsStorageService] ✓ OPFS initialized: OPFS Worker initialized successfully
[OpfsStorageService] ✓ Capacity: 10, Files: 0
[OpfsStorageService] ✓ JSImport interop initialized
[OpfsStorageService] ✓ VFS tracking initialized (page size: 4096 bytes)
[OpfsStorageService] Persisting (incremental): TodoDb.db - 2 dirty pages
[OpfsStorageService] ✓ JSImport: Written 2 pages (8 KB)
[OpfsStorageService] ✓ Persisted 2 pages (8 KB)
[OpfsStorageService] No dirty pages for TodoDb.db, skipping persist
```

## Recommendations

### Development
Use `OpfsLogLevel.Debug` during development to see all operations:
```csharp
#if DEBUG
    opfsLogLevel: OpfsLogLevel.Debug
#else
    opfsLogLevel: OpfsLogLevel.Warning
#endif
```

### Production
Use `OpfsLogLevel.Warning` (default) or `OpfsLogLevel.Error` for minimal output:
```csharp
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(
    options => options.UseSqlite("Data Source=app.db"),
    opfsLogLevel: OpfsLogLevel.Warning); // Only show problems
```

### Troubleshooting
Temporarily increase to `OpfsLogLevel.Info` or `OpfsLogLevel.Debug`:
```csharp
@code {
    protected override async Task OnInitializedAsync()
    {
        // Enable detailed logging for this session
        OpfsStorage.LogLevel = OpfsLogLevel.Debug;

        await OpfsStorage.InitializeAsync();
        // ... investigate issues ...
    }
}
```

## Browser Console Output

The log level only affects C# console output. JavaScript console logs from the OPFS worker are separate and controlled by the browser.

To see worker logs:
1. Open Browser DevTools (F12)
2. Console tab
3. Look for messages prefixed with:
   - `[OPFS Worker]`
   - `[OPFS Initializer]`
   - `[OPFS Interop]`
   - `[VFS Tracking]`

## Performance Impact

Log levels have **minimal performance impact**:
- Each log call checks a simple boolean condition
- No string formatting occurs if log level is disabled
- `None` level has zero overhead (all checks short-circuit)

Example:
```csharp
// This has near-zero cost if LogLevel < Debug
LogDebug($"Processing {items.Count} items"); // String interpolation only happens if needed
```

## Best Practices

1. **Start with Warning** - Default is appropriate for most cases
2. **Use Debug sparingly** - Only when actively troubleshooting
3. **Production: Warning or Error** - Keep noise minimal
4. **Dynamic adjustment** - Change at runtime when needed
5. **Conditional compilation** - Use `#if DEBUG` for development builds
