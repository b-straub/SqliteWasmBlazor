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

### Worker Log Level

```csharp
// Set worker log level (affects SQL logging in browser console)
SqliteWasmLogger.SetLogLevel(LogLevel.Warning);
```

### EF Core Logging

```csharp
// In Program.cs
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Infrastructure", LogLevel.Error);
```

### Per-Connection Logging

```csharp
// Specify log level in connection constructor
var connection = new SqliteWasmConnection("Data Source=MyDb.db", LogLevel.Debug);
```

Available log levels: `Trace`, `Debug`, `Information`, `Warning` (default), `Error`, `Critical`, `None`

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
