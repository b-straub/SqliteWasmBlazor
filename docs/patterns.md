# Recommended Patterns

## Multi-View Instead of Multi-Tab

### Why Multiple Browser Tabs Don't Work with OPFS

OPFS (Origin Private File System) uses exclusive synchronous access handles - only one tab can hold a write lock on the database at a time. Opening multiple browser tabs creates multiple independent WASM runtimes, each trying to acquire exclusive access to the same database file. This is a fundamental browser API constraint, not a library limitation.

### The Solution: Floating Windows in a Single Tab

Instead of multiple browser tabs, use **multiple views within a single PWA tab**. This mirrors how desktop applications work - one process, one database connection, multiple windows on the same data.

```
Single Browser Tab (PWA)
├── View 1: Table View     ← Own DbContext per operation
├── View 2: Card View      ← Own DbContext per operation
├── View 3: Stats View     ← Own DbContext per operation
│
├── IDbContextFactory<T>   ← Creates short-lived contexts
└── DataNotifier           ← Broadcasts change events
```

### Implementation Pattern

**1. Use `IDbContextFactory<T>` for independent contexts per operation:**

```csharp
// Each view creates short-lived DbContext instances - no shared state conflicts
private async Task AddTodoAsync()
{
    await using var context = await DbContextFactory.CreateDbContextAsync();
    context.TodoItems.Add(newTodo);
    await context.SaveChangesAsync();

    // Notify other views that data changed
    Notifier.NotifyDataChanged();
}
```

**2. Use a lightweight notification service to synchronize views:**

```csharp
// Singleton service - broadcasts change events across all open views
public sealed class TodoDataNotifier
{
    public event Action? OnDataChanged;

    public void NotifyDataChanged()
    {
        OnDataChanged?.Invoke();
    }
}
```

**3. Each view subscribes and re-queries on change:**

```csharp
protected override void OnInitialized()
{
    Notifier.OnDataChanged += OnDataChanged;
}

private void OnDataChanged()
{
    _ = InvokeAsync(async () =>
    {
        await LoadDataAsync();   // Re-query with fresh DbContext
        StateHasChanged();
    });
}
```

This is the standard EF Core `IDbContextFactory` pattern - not a WASM workaround. It works the same way in ASP.NET, Blazor Server, and desktop applications. Each operation gets a short-lived `DbContext`, avoiding tracking conflicts and threading issues that arise from long-lived shared contexts.

The demo application includes a complete Multi-View example using `SqliteWasmBlazor.WindowHelper` - a lightweight Razor Class Library that adds draggable, resizable floating behavior to standard MudBlazor dialogs via JS interop. Navigate to `/multiview` in the demo to see it in action.

## Data Initialization Without Page Reload

### The Anti-Pattern: Reload After Initial Data Fetch

A common mistake in OPFS-backed PWAs is this sequence on first launch:

```
1. App starts → acquires OPFS database lock
2. Database is empty → fetch data from remote API
3. Insert fetched data into SQLite
4. Full page reload (NavigationManager.NavigateTo("/", forceLoad: true))
5. App re-starts → tries to acquire OPFS lock again
```

On faster machines this may work because the lock is released before the reload completes. On slower machines, the reload races the lock release - the new runtime attempts to acquire the OPFS lock while the previous runtime is still tearing down. This results in the database being inaccessible, requiring a manual reload to recover.

This is a **fundamental architectural issue**, not a performance bug. A full page reload in a Blazor WASM PWA tears down the entire .NET runtime, the Web Worker, and the OPFS connection, then re-initializes everything from scratch. This is expensive (~100-200ms minimum) and inherently racy with OPFS lock cleanup.

### The Correct Pattern: Fetch, Insert, Refresh View

Never reload the page to display new data. Instead, update the view state in-place:

```csharp
// Correct: fetch, insert, refresh view - no reload
protected override async Task OnInitializedAsync()
{
    await using var context = await DbContextFactory.CreateDbContextAsync();

    var hasData = await context.TodoItems.AnyAsync();
    if (!hasData)
    {
        // Fetch from remote API
        var remoteData = await HttpClient.GetFromJsonAsync<List<TodoItemDto>>("api/todos");
        if (remoteData is not null)
        {
            // Insert into local SQLite
            foreach (var dto in remoteData)
            {
                context.TodoItems.Add(dto.ToEntity());
            }
            await context.SaveChangesAsync();
        }
    }

    // Load and display - no reload needed
    _todos = await context.TodoItems.OrderBy(t => t.UpdatedAt).ToListAsync();
}
```

```
Correct flow:
1. App starts → acquires OPFS lock (once)
2. Check if data exists
3. If empty → fetch from API, insert into SQLite
4. Load data into component state
5. StateHasChanged() → UI updates
```

### Why This Matters for PWAs

A Blazor WASM PWA is not a traditional web application where each page navigation is a cheap HTTP request. It is a **client-side application** with:

- A full .NET runtime loaded into memory
- A Web Worker managing OPFS file handles
- Exclusive database locks that require graceful teardown

Treating it like a server-rendered app (reload to refresh) breaks the single-runtime assumption that OPFS depends on. The correct mental model is a desktop application: initialize once, keep the process running, update the UI reactively.
