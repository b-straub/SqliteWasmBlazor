# Advanced Features

## Migrations

EF Core migrations work with SqliteWasmBlazor, but require special configuration due to WebAssembly limitations.

### Project Structure Recommendation

Put your DbContext and models in a **separate project** (e.g., `YourApp.Models`):

- Reference this project from your Blazor WebAssembly project
- Configure `Microsoft.EntityFrameworkCore.Design` with minimal assets:

```xml
<!-- In YourApp.Models.csproj -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0">
    <IncludeAssets>runtime; analyzers;</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

This prevents design-time assets from being published with your WebAssembly app, which would cause errors.

### Generate and Apply Migrations

```bash
# Generate migration (run from models project directory)
dotnet ef migrations add InitialCreate --context TodoDbContext

# Apply migrations at runtime (in your Blazor app)
await dbContext.Database.MigrateAsync();
```

The `InitializeSqliteWasmDatabaseAsync` extension method automatically applies pending migrations during app startup.

## Full-Text Search (FTS5)

SqliteWasmBlazor supports SQLite's FTS5 (Full-Text Search 5) virtual tables for powerful text search capabilities.

### Define FTS5 Entity

```csharp
public class FTSTodoItem
{
    public int RowId { get; set; }
    public string? Match { get; set; }
    public double Rank { get; set; }
    public TodoItem? TodoItem { get; set; }
}
```

### Configure in DbContext

```csharp
modelBuilder.Entity<FTSTodoItem>(entity =>
{
    entity.HasNoKey();
    entity.ToTable("FTSTodoItem");
    entity.Property(e => e.Match).HasColumnName("FTSTodoItem");
});
```

### Create FTS5 Table via Migration

Manually edit the migration file:

```csharp
migrationBuilder.Sql(@"
    CREATE VIRTUAL TABLE FTSTodoItem USING fts5(
        Title, Description,
        content='TodoItems',
        content_rowid='Id'
    );
");
```

### Search with Highlighting

```csharp
var results = dbContext.Database
    .SqlQuery<TodoItemSearchResult>($@"
        SELECT
            t.Id, t.Title, t.Description,
            highlight(FTSTodoItem, 0, '<mark>', '</mark>') AS HighlightedTitle,
            highlight(FTSTodoItem, 1, '<mark>', '</mark>') AS HighlightedDescription,
            rank AS Rank
        FROM FTSTodoItem
        INNER JOIN TodoItems t ON FTSTodoItem.rowid = t.Id
        WHERE FTSTodoItem MATCH {searchQuery}
        ORDER BY rank")
    .AsNoTracking();
```

### FTS5 Features

- Full-text search across multiple columns with relevance ranking
- `highlight()` function for marking search matches in full text
- `snippet()` function for contextual excerpts with configurable token limits
- Automatic query sanitization to handle special characters safely
- Support for phrase searches, prefix matching, and boolean operators

See the demo application for a complete FTS5 implementation example.

## JSON Collections

Store complex types as JSON in SQLite:

```csharp
public class MyEntity
{
    public int Id { get; set; }
    public List<int> Numbers { get; set; }
}

// In OnModelCreating:
entity.Property(e => e.Numbers)
    .HasConversion(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions?)null) ?? new()
    )
    .Metadata.SetValueComparer(
        new ValueComparer<List<int>>(
            (c1, c2) => c1!.SequenceEqual(c2!),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToList()
        )
    );
```

The `ValueComparer` is essential for EF Core to detect changes in collection properties.

## Logging Configuration

### SQL Command Logging (opt-in)

Executed SQL and its parameter values are **not** logged unless you ask for
them — schema and data would otherwise reach the browser console in production:

```csharp
builder.Services.AddSqliteWasm(o => o.EnableCommandSqlLogging = true);
```

This is the only switch that lets SQL text or parameter values reach the
console, on either side of the worker boundary — managed and worker alike. The
log level is orthogonal: it decides how verbose logging is, this decides whether
query *content* is emitted at all. So tracing at `Debug` shows request timings
and row counts while schema and parameter values stay withheld.

### Worker Log Level

One level, set once, for the worker and the main-thread half of the bridge:

```csharp
// In Program.cs, before InitializeSqliteWasmAsync /
// InitializeSqliteWasmDatabaseAsync so worker startup and the database opens
// are covered too.
SqliteWasmLogger.SetLogLevel(LogLevel.Debug);
```

Levels: `Trace`, `Debug`, `Information`, `Warning` (default), `Error`,
`Critical`, `None`. The managed side takes the new level immediately; the JS
bridge and the worker receive it as soon as the worker is up, so calling this
before initialization is not only allowed, it is the only way to trace
initialization.

### EF Core Logging

```csharp
// In Program.cs
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Infrastructure", LogLevel.Error);
```

## Custom EF Core Functions

All EF Core functions are implemented for full compatibility:

| Category | Functions |
|----------|-----------|
| **Arithmetic** | `ef_add`, `ef_divide`, `ef_multiply`, `ef_mod`, `ef_negate` |
| **Comparison** | `ef_compare` |
| **Aggregates** | `ef_sum`, `ef_avg`, `ef_min`, `ef_max` |
| **Pattern Matching** | `regexp` (for `Regex.IsMatch()`) |
| **Collation** | `EF_DECIMAL` (for proper decimal sorting) |

### Decimal Arithmetic Example

```csharp
// This LINQ query uses ef_multiply under the hood
var expensive = await dbContext.Products
    .Where(p => p.Price * 1.2m > 100m)
    .ToListAsync();
```

EF Core automatically translates decimal operations to the appropriate `ef_*` functions, ensuring correct arithmetic in SQLite (which doesn't have native decimal support).

## Moving Databases In and Out

`ISqliteWasmDatabaseService` carries the whole file surface — one database or
many, in or out, to a `Stream` or straight to a browser download. **No path
holds a database in managed memory.** That is not an optimisation: mobile
Safari kills the page when a multi-hundred-megabyte export lands in memory, and
a large import assembled on the main thread takes the pool's access handles
down with it.

```csharp
@inject ISqliteWasmDatabaseService DatabaseService

// Straight to a download. The worker copies the database into an OPFS staging
// file in slices and the browser saves from that disk-backed File.
await DatabaseService.ExportDatabaseToDownloadAsync("TodoDb.db", "todo-backup.db");

// Several databases as one .dbs envelope (MessagePack [name, bytes] tuples).
await DatabaseService.ExportDatabasesToDownloadAsync(names, "backup.dbs");

// Same bytes as the download, into a Stream you own.
await using var file = File.Create(path);
await DatabaseService.ExportDatabaseToStreamAsync("TodoDb.db", file);

// Import, one chunk at a time into the worker.
await using var stream = picked.OpenReadStream(maxAllowedSize: picked.Size);
await DatabaseService.ImportDatabaseFromStreamAsync(
    "TodoDb.db", stream, picked.Size);
```

Every one of these closes the database in the worker — exports for a consistent
snapshot, imports because the slot is being replaced under it. Connection-state
tracking re-opens it on the next EF Core query; there is no manual dance.

With `SqliteWasmBlazor.Crypto` loaded the worker is state-aware and the calls do
not change: a plain pool reads and writes plain pages, an unlocked encrypted
pool decrypts on the way out and rekeys on the way in. A locked pool refuses
with `PoolOperationRejectedException` — unlock first.

> **Exports are plaintext.** On an encrypted pool these emit a plain `.db` any
> SQLite tool can open, which is the point of an export but worth saying out
> loud in your UI before the user clicks. The `.eds` envelope on
> `IEncryptedSqliteWasmDatabaseService` is the encrypted-backup path.

### Validating What an Import Brought

Both import methods take an optional `validateImported` delegate. What the
import would replace is parked under a suffixed name, the incoming file is
written under the database's **real** name, and the delegate opens it and
decides:

```csharp
await DatabaseService.ImportDatabaseFromStreamAsync(
    "TodoDb.db", stream, size,
    async (imported, ct) =>
    {
        await using var ctx = await DbContextFactory.CreateDbContextAsync(ct);
        // Takes the database name for the error message, not a token.
        await ctx.ValidateImportedSchemaAsync(imported);  // throws SchemaMismatchException
    });
```

Throw and the pool goes back exactly as it was — the parks are renamed back,
metadata-only, so what returns is byte-identical. Return and the import counts:
the parks are dropped and the host's migrations re-run, so a file carrying an
older schema is brought up to date and an owned database the import omitted is
re-created before the next query hits it.

Writing under the real name is not a detail. On an encrypted pool, page AAD
binds ciphertext to the database path, so a file written under a staging name
would stop decrypting the moment it was moved.

Don't roll this by hand with `RenameDatabaseAsync`: a rename onto an occupied
name leaves the occupant's slot claimed but unreachable, and delete-then-rename
leaves a window where the backup and the file that displaced it both stand with
nothing to say which one is the database. The import does it as one pool-level
replace.

### Telling the Library Which Databases You Own

`MigrateAsync` and the schema gate need something only the host knows — which
databases the app opens, and what their schema is supposed to be. Implement
`IHostDatabaseService` and register it once:

```csharp
builder.Services.AddHostDatabaseService<MyHostDatabaseService>();
```

Apps using `SqliteWasmBlazor.Crypto.UI` implement `IHostRecoveryService`
instead — the same three members plus `IsAvailable` and `ResetAsync` for the
panels' recovery affordance — and register with `AddHostRecoveryService<THost>()`,
which binds one instance to both interfaces.

### Downloads Installed on a Home Screen

A web app installed on a Home Screen has no downloads folder, so WebKit drops the
`download` attribute and navigates to the file instead: a finished export arrives
on a full-screen OS intermediate screen — an icon, a size, and "Open in
&lt;app&gt;" — and the user saves it from that screen's share menu. The export
itself is unaffected, because WebKit streams the disk-backed staging file onto
that screen, so it holds at any size.

Worth a line of UI copy above your export controls: it is the one place where the
export looks like it failed while having entirely succeeded, and the OS screen
covers anything you could say afterwards. Handing the file to `navigator.share`
instead is a trap worth naming — the share sheet has to materialise the file to
pass it to another process, so it dies on a large database, while this path does
not.

### Schema Validation

`ValidateImportedSchemaAsync` is the check itself, usable outside an import:

```csharp
using SqliteWasmBlazor;

await using var ctx = await DbContextFactory.CreateDbContextAsync();
await ctx.ValidateImportedSchemaAsync("TodoDb.db");
```

The argument is a display name for the error message. Required tables come from
the design-time EF model — no hardcoded strings — with owned entities and
anything excluded from migrations (FTS5 virtual tables) skipped. A mismatch
throws `SchemaMismatchException`, an `InvalidOperationException` carrying
`MissingTables` so a host can phrase the refusal in its own language.

`IHostDatabaseService.ValidateSchemaAsync` is the seam a host implements; this
extension is what it normally calls.
