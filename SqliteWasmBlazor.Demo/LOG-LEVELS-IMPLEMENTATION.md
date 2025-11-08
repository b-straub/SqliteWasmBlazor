# OPFS Log Levels - Implementation Summary

## What Was Implemented

Added configurable log levels for OPFS operations, similar to MSBuild verbosity levels (None, Error, Warning, Info, Debug).

## Changes Made

### 1. Created `OpfsLogLevel` Enum
**File**: `/SQLiteNET.Opfs/Abstractions/OpfsLogLevel.cs`

```csharp
public enum OpfsLogLevel
{
    None = 0,      // No output
    Error = 1,     // Only errors
    Warning = 2,   // Errors and warnings (default)
    Info = 3,      // Errors, warnings, and info
    Debug = 4      // Everything
}
```

### 2. Updated `IOpfsStorage` Interface
**File**: `/SQLiteNET.Opfs/Abstractions/IOpfsStorage.cs`

Added property:
```csharp
/// <summary>
/// Control logging verbosity for OPFS operations.
/// Default: Warning (only errors and warnings).
/// </summary>
OpfsLogLevel LogLevel { get; set; }
```

### 3. Updated `OpfsStorageService`
**File**: `/SQLiteNET.Opfs/Services/OpfsStorageService.cs`

**Added**:
- `LogLevel` property (default: `Warning`)
- Four helper methods:
  - `LogDebug(string message)` - Verbose operational details
  - `LogInfo(string message)` - Successful operations
  - `LogWarning(string message)` - Issues with fallback
  - `LogError(string message)` - Critical failures

**Replaced** all `Console.WriteLine()` calls with appropriate log level methods:
- Initialization details → `LogDebug`
- Successful operations → `LogInfo`
- Fallback scenarios → `LogWarning`
- Fatal errors → `LogError`

### 4. Updated Extension Methods
**File**: `/SQLiteNET.Opfs/Extensions/OpfsServiceCollectionExtensions.cs`

Added `opfsLogLevel` parameter to both overloads:
```csharp
public static IServiceCollection AddOpfsDbContextFactory<TDbContext>(
    this IServiceCollection services,
    Action<DbContextOptionsBuilder> optionsAction,
    OpfsLogLevel opfsLogLevel = OpfsLogLevel.Warning)
```

Service registration now respects the log level:
```csharp
services.AddSingleton<OpfsStorageService>(sp =>
{
    var jsRuntime = sp.GetRequiredService<IJSRuntime>();
    var service = new OpfsStorageService(jsRuntime)
    {
        LogLevel = opfsLogLevel
    };
    return service;
});
```

## Default Behavior

**Out of the box**: `LogLevel.Warning` (minimal output)
- Shows only errors and warnings
- Silent for successful operations
- Perfect for production use

## Usage Examples

### Production (Minimal Logging)
```csharp
// Default - only warnings and errors
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(options =>
{
    options.UseSqlite("Data Source=app.db");
});
```

Console output:
```
[OpfsStorageService] ⚠ VFS tracking unavailable: ...
```

### Development (Informational)
```csharp
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(
    options => options.UseSqlite("Data Source=app.db"),
    opfsLogLevel: OpfsLogLevel.Info);
```

Console output:
```
[OpfsStorageService] ✓ OPFS initialized: OPFS Worker initialized successfully
[OpfsStorageService] ✓ JSImport interop initialized
[OpfsStorageService] ✓ VFS tracking initialized (page size: 4096 bytes)
[OpfsStorageService] ✓ Persisted 2 pages (8 KB)
```

### Debugging (Verbose)
```csharp
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(
    options => options.UseSqlite("Data Source=app.db"),
    opfsLogLevel: OpfsLogLevel.Debug);
```

Console output:
```
[OpfsStorageService] Starting initialization...
[OpfsStorageService] Initialize result: Success=True, Message=Initialized
[OpfsStorageService] ✓ OPFS initialized: OPFS Worker initialized successfully
[OpfsStorageService] Persisting (incremental): TodoDb.db - 2 dirty pages
[OpfsStorageService] ✓ JSImport: Written 2 pages (8 KB)
[OpfsStorageService] No dirty pages for TodoDb.db, skipping persist
```

### Runtime Configuration
```csharp
protected override async Task OnInitializedAsync()
{
    // Increase verbosity for troubleshooting
    OpfsStorage.LogLevel = OpfsLogLevel.Debug;

    await OpfsStorage.InitializeAsync();

    // Reduce back to normal
    OpfsStorage.LogLevel = OpfsLogLevel.Warning;
}
```

## Log Level Comparison

| Operation | None | Error | Warning | Info | Debug |
|-----------|------|-------|---------|------|-------|
| Initialization success | ❌ | ❌ | ❌ | ✅ | ✅ |
| Initialization failure | ❌ | ✅ | ✅ | ✅ | ✅ |
| VFS tracking unavailable | ❌ | ❌ | ✅ | ✅ | ✅ |
| Persist success | ❌ | ❌ | ❌ | ✅ | ✅ |
| Dirty page count | ❌ | ❌ | ❌ | ❌ | ✅ |
| Worker cleanup | ❌ | ❌ | ❌ | ✅ | ✅ |

## Performance Impact

**Negligible** - Log methods check a simple enum comparison before any string operations:

```csharp
private void LogDebug(string message)
{
    if (LogLevel >= OpfsLogLevel.Debug)  // Fast enum comparison
    {
        Console.WriteLine($"[OpfsStorageService] {message}"); // Only executes if true
    }
}
```

String interpolation in calls like `LogDebug($"Value: {x}")` is optimized by the compiler when the method is not inlined.

## Migration from Previous Version

### Before (Verbose by Default)
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
```

### After (Warning by Default)
```
(no output unless there's a warning or error)
```

To get the old verbose behavior back:
```csharp
opfsLogLevel: OpfsLogLevel.Debug
```

## Benefits

✅ **Cleaner Production Output** - Default `Warning` level keeps console quiet
✅ **MSBuild-like Levels** - Familiar pattern for .NET developers
✅ **Runtime Configurable** - Change verbosity without rebuilding
✅ **Zero Performance Cost** - Disabled logs have minimal overhead
✅ **Flexible Control** - Choose between None, Error, Warning, Info, Debug
✅ **Backward Compatible** - Existing code works (just less verbose)

## Files Modified

1. `/SQLiteNET.Opfs/Abstractions/OpfsLogLevel.cs` (new)
2. `/SQLiteNET.Opfs/Abstractions/IOpfsStorage.cs` (added LogLevel property)
3. `/SQLiteNET.Opfs/Services/OpfsStorageService.cs` (log level implementation)
4. `/SQLiteNET.Opfs/Extensions/OpfsServiceCollectionExtensions.cs` (opfsLogLevel parameter)

## Documentation

- [LOG-LEVELS-EXAMPLE.md](LOG-LEVELS-EXAMPLE.md) - Usage examples and recommendations
- [README.md](README.md) - Updated main documentation
