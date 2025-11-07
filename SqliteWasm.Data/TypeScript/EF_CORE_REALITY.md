# EF Core SQLite Reality Check

## What EF Core Actually Does with Decimals

After testing and examining the SQL logs, here's what EF Core **actually** does with decimal operations in SQLite:

### ❌ What Does NOT Work (Client-Side Evaluation)

EF Core **does NOT** translate these operations to `ef_*` functions:

```csharp
// These are evaluated CLIENT-SIDE (loads all data into memory first)
var results = await context.TypeTests
    .Where(e => e.DecimalValue + 50 > 100)  // Loads ALL rows, filters in C#
    .ToListAsync();

var results = await context.TypeTests
    .Where(e => e.DecimalValue * 0.9m < 100)  // Loads ALL rows, filters in C#
    .ToListAsync();
```

### ✅ What DOES Work (Server-Side/SQL)

1. **Simple Comparisons**: Work fine, no special functions needed
```csharp
var results = await context.TypeTests
    .Where(e => e.DecimalValue > 100)  // Translates to: WHERE DecimalValue > 100
    .ToListAsync();
```

2. **Aggregates**: Use built-in SQLite functions (SUM, AVG, MIN, MAX)
```csharp
var sum = await context.TypeTests.SumAsync(e => e.DecimalValue);
// Translates to: SELECT SUM(DecimalValue) FROM TypeTests
```

3. **Ordering**: Works with simple comparisons
```csharp
var ordered = await context.TypeTests
    .OrderBy(e => e.DecimalValue)  // Translates to: ORDER BY DecimalValue
    .ToListAsync();
```

4. **Regex**: Translates to `REGEXP` operator
```csharp
var matches = await context.TypeTests
    .Where(e => Regex.IsMatch(e.StringValue, "pattern"))
    // Translates to: WHERE StringValue REGEXP 'pattern'
    .ToListAsync();
```

## When Are ef_* Functions Actually Used?

The `ef_*` functions are registered by EF Core's official SQLite provider (Microsoft.Data.Sqlite) but are **only used in very specific scenarios**:

1. They are NOT used for LINQ decimal arithmetic (those are client-evaluated)
2. They ARE used when EF Core needs to perform decimal operations in SQL for internal purposes
3. They ARE used if you execute raw SQL that explicitly calls them

## The Warning Explained

```
A connection of an unexpected type (SqliteWasmConnection) is being used.
The SQL functions prefixed with 'ef_' could not be created automatically.
```

This warning appears because:
1. EF Core checks if the connection type is `Microsoft.Data.Sqlite.SqliteConnection`
2. If it is, EF Core automatically registers `ef_*` functions
3. Our custom `SqliteWasmConnection` is not recognized, so auto-registration doesn't happen
4. We manually register them in the worker instead

## Recommendation

For this project:
1. **Keep the manual `ef_*` function registration** for completeness and potential future EF Core updates
2. **Suppress the warning** as it's informational only
3. **Don't create tests for client-evaluated operations** - they don't use the functions anyway
4. **Focus tests on what actually translates to SQL**: comparisons, aggregates, regex

## References

- [EF Core Issue #19982](https://github.com/dotnet/efcore/issues/19982) - Decimal arithmetic translation limitations
- [EF Core Issue #18593](https://github.com/dotnet/efcore/issues/18593) - Decimal ordering issues
- [Microsoft Learn: SQLite Limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
