# Multi-Database Support

## Overview

SqliteWasmBlazor supports running multiple independent SQLite databases simultaneously in the same Web Worker. Each database gets its own OPFS file, connection, and EF Core `DbContext` - fully isolated with no shared state.

## Architecture

```
.NET WASM (Main Thread)
├── TodoDbContext ────► SqliteWasmConnection("Data Source=TodoDb.db")
├── NoteDbContext ────► SqliteWasmConnection("Data Source=NotesDb.db")
│
└── SqliteWasmWorkerBridge (Singleton)
    └── postMessage({ database: "TodoDb.db", sql: "..." })
    └── postMessage({ database: "NotesDb.db", sql: "..." })

Web Worker (OPFS Thread)
├── openDatabases: Map<string, OpfsSAHPoolDb>
│   ├── "TodoDb.db"  → /databases/TodoDb.db   (independent WAL, PRAGMAs)
│   └── "NotesDb.db" → /databases/NotesDb.db  (independent WAL, PRAGMAs)
└── schemaCache: Map<"dbName:tableName", columnTypes>
```

All database operations route through a single worker bridge singleton. The worker maintains a `Map<string, any>` of open databases, each with independent connections, WAL journals, and PRAGMA configurations.

## Setup

### 1. Define Separate DbContexts

Each database needs its own `DbContext`:

```csharp
public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions<TodoDbContext> options) : base(options) { }
    public DbSet<TodoItem> TodoItems { get; set; }
}

public class NoteDbContext : DbContext
{
    public NoteDbContext(DbContextOptions<NoteDbContext> options) : base(options) { }
    public DbSet<Note> Notes { get; set; }
}
```

### 2. Register Both in Program.cs

```csharp
// Database 1: TodoDb.db
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db");
    options.UseSqliteWasm(connection);
});

// Database 2: NotesDb.db
builder.Services.AddDbContextFactory<NoteDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=NotesDb.db");
    options.UseSqliteWasm(connection);
});

builder.Services.AddSqliteWasm();

var host = builder.Build();

// Initialize both databases with migration support
await host.Services.InitializeSqliteWasmDatabaseAsync<TodoDbContext>();
await host.Services.InitializeSqliteWasmDatabaseAsync<NoteDbContext>();
```

### 3. Create Migrations for Each Context

Each `DbContext` needs its own design-time factory and migration folder:

```csharp
public class NoteDbContextFactory : IDesignTimeDbContextFactory<NoteDbContext>
{
    public NoteDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NoteDbContext>();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        return new NoteDbContext(optionsBuilder.Options);
    }
}
```

Generate migrations into separate folders:

```bash
dotnet ef migrations add InitialCreate --context TodoDbContext --output-dir Migrations
dotnet ef migrations add InitialCreate --context NoteDbContext --output-dir NoteMigrations
```

### 4. Use in Components

Inject both factories and use them independently:

```razor
@inject IDbContextFactory<TodoDbContext> TodoFactory
@inject IDbContextFactory<NoteDbContext> NoteFactory

@code {
    private async Task LoadData()
    {
        // Each context operates on its own database
        await using var todoCtx = await TodoFactory.CreateDbContextAsync();
        var todos = await todoCtx.TodoItems.ToListAsync();

        await using var noteCtx = await NoteFactory.CreateDbContextAsync();
        var notes = await noteCtx.Notes.ToListAsync();
    }
}
```

## Cross-Database References

Since databases are independent, there are no foreign key constraints between them. Use **loose Guid references** - the same pattern used in microservices architectures:

```csharp
public class Note
{
    [Key]
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }

    // Loose reference to a Todo in another database - no FK constraint
    public Guid? TodoId { get; set; }
}
```

Resolve references at the application level:

```csharp
var notes = await noteCtx.Notes.ToListAsync();
var todos = await todoCtx.Todos.ToListAsync();

// Join in memory
foreach (var note in notes.Where(n => n.TodoId is not null))
{
    var linkedTodo = todos.FirstOrDefault(t => t.Id == note.TodoId);
    // linkedTodo may be null if the referenced todo was deleted
}
```

## Database Reset

When resetting databases (e.g., on schema mismatch), delete all database files:

```csharp
await DatabaseService.DeleteDatabaseAsync("TodoDb.db");
await DatabaseService.DeleteDatabaseAsync("NotesDb.db");

// Recreate with migrations
await using var todoCtx = await TodoFactory.CreateDbContextAsync();
await todoCtx.Database.MigrateAsync();

await using var noteCtx = await NoteFactory.CreateDbContextAsync();
await noteCtx.Database.MigrateAsync();
```

## When to Use Multiple Databases

**Good reasons to separate:**
- Different data domains with independent lifecycles (e.g., user content vs. app configuration)
- Different sync/backup strategies per database
- Isolating large datasets from frequently-accessed small tables
- Different retention policies (one database can be cleared without affecting the other)

**When a single database is sufficient:**
- Related entities that benefit from FK constraints and JOIN queries
- Data that shares transactions (cross-database transactions are not possible)
- Small to medium datasets where separation adds complexity without benefit
