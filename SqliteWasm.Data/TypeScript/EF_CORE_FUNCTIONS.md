# EF Core Functions Implementation

## Overview

This implementation provides EF Core's SQLite scalar and aggregate functions (`ef_*` prefixed functions) for the worker-based OPFS architecture. These functions enable full LINQ query support for decimal arithmetic, aggregates, comparisons, and regex patterns.

## Architecture

### Why Manual Registration is Required

EF Core automatically registers `ef_*` functions when using standard `Microsoft.Data.Sqlite.SqliteConnection`. However, our architecture uses:
1. **Custom Connection Type**: `SqliteWasmConnection` (not recognized by EF Core)
2. **Worker-Based Execution**: SQL runs in a Web Worker with `sqlite-wasm` (not in .NET)
3. **Dual-Instance Pattern**: .NET WASM (main thread) + Web Worker (OPFS persistence)

Therefore, we manually register all required functions in the worker's SQLite instance.

## Registered Functions

### Scalar Functions (Arithmetic & Logic)
- `ef_add(left, right)` - Addition
- `ef_divide(dividend, divisor)` - Division (NULL if divisor is 0)
- `ef_multiply(left, right)` - Multiplication
- `ef_negate(value)` - Negation (unary minus)
- `ef_mod(dividend, divisor)` - Modulo (NULL if divisor is 0)
- `ef_compare(left, right)` - Three-way comparison (-1, 0, 1)
- `regexp(pattern, input)` - Regular expression matching

### Aggregate Functions (Window-compatible)
- `ef_sum(value)` - Sum of values
- `ef_avg(value)` - Average of values
- `ef_min(value)` - Minimum value
- `ef_max(value)` - Maximum value

### Collations
- `EF_DECIMAL(x, y)` - Numeric comparison for TEXT-stored decimals

## Implementation Details

### Location
- **Registration**: `SqliteWasm.Data/TypeScript/ef-core-functions.ts`
- **Called from**: `sqlite-worker.ts` line 226 (on database open)
- **Reference**: EF Core `SqliteRelationalConnection.cs` (lines 100-189)

### Null Semantics
All functions follow SQL NULL semantics:
- If any input is NULL, result is NULL (except aggregates)
- Division/modulo by zero returns NULL
- Aggregates ignore NULL values

### Precision
- Uses JavaScript's native `number` type (IEEE 754 double)
- Provides ~15-17 digits of decimal precision
- Sufficient for most financial/business applications
- For arbitrary precision, would need decimal.js (not implemented - see notes below)

## Usage in LINQ Queries

### Arithmetic Operations
```csharp
// Translates to: SELECT * FROM TypeTests WHERE (DecimalValue + 50) > 100
var results = await context.TypeTests
    .Where(e => e.DecimalValue + 50 > 100)
    .ToListAsync();

// Translates to: SELECT * FROM TypeTests WHERE (DecimalValue * 0.9) < 100
var discounted = await context.TypeTests
    .Where(e => e.DecimalValue * 0.9m < 100)
    .ToListAsync();
```

### Aggregate Functions
```csharp
// Translates to: SELECT ef_sum(DecimalValue), ef_avg(DecimalValue) FROM TypeTests
var stats = await context.TypeTests
    .GroupBy(e => 1)
    .Select(g => new {
        Total = g.Sum(e => e.DecimalValue),
        Average = g.Average(e => e.DecimalValue)
    })
    .FirstAsync();
```

### Comparison & Ordering
```csharp
// Translates to: SELECT * FROM TypeTests WHERE ef_compare(DecimalValue, 100) > 0 ORDER BY DecimalValue
var ordered = await context.TypeTests
    .Where(e => e.DecimalValue > 100)
    .OrderBy(e => e.DecimalValue)
    .ToListAsync();
```

### Regex Matching
```csharp
// Translates to: SELECT * FROM TypeTests WHERE regexp('^[a-z]+@[a-z]+\.[a-z]+$', StringValue)
var emails = await context.TypeTests
    .Where(e => Regex.IsMatch(e.StringValue, @"^[a-z]+@[a-z]+\.[a-z]+$"))
    .ToListAsync();
```

## Testing

Comprehensive tests are located in:
- `SQLiteNET.Opfs.TestApp/TestInfrastructure/Tests/EFCoreFunctions/`

Test categories:
1. **DecimalArithmeticTest** - Addition, multiplication, division, negation, modulo
2. **DecimalAggregatesTest** - Sum, average, min, max
3. **DecimalComparisonTest** - Greater than, less than, ordering, ranges
4. **RegexPatternTest** - Email patterns, substring matching
5. **ComplexDecimalQueryTest** - Combined arithmetic, aggregates, and comparisons

Run tests via the Test App UI to verify all functions work correctly.

## Warning Suppression

The following warning is suppressed in non-DEBUG builds:
```
warn: Microsoft.EntityFrameworkCore.Infrastructure[30100]
      A connection of an unexpected type (SqliteWasmConnection) is being used.
      The SQL functions prefixed with 'ef_' could not be created automatically.
      Manually define them if you encounter errors while querying.
```

This is **informational only** - all `ef_*` functions ARE correctly registered in the worker.

## Decimal.js Considerations

### Why decimal.js is NOT used:
1. **sqlite-wasm limitation**: The underlying SQLite WASM uses native numbers internally
2. **Incompatible types**: decimal.js returns Decimal objects, not numbers
3. **Limited benefit**: Would need to modify entire sqlite-wasm build to preserve precision
4. **Performance**: Native numbers are faster and sufficient for most use cases

### When you MIGHT need decimal.js:
- Financial applications requiring exact decimal arithmetic (e.g., currency calculations)
- Scientific applications needing >15 digits of precision
- Cases where rounding errors accumulate significantly

### How to implement (if needed):
1. Install: `npm install decimal.js`
2. Import: `import Decimal from 'decimal.js';`
3. Replace arithmetic operations: `return new Decimal(left).plus(right).toNumber();`
4. **WARNING**: This only affects function calculations - SQLite storage still uses REAL (double)

## References

- EF Core source: [SqliteRelationalConnection.cs](https://github.com/dotnet/efcore/blob/main/src/EFCore.Sqlite.Core/Storage/Internal/SqliteRelationalConnection.cs)
- SQLite functions: [sqlite-wasm createFunction API](https://sqlite.org/wasm/doc/trunk/api-c-style.md#sqlite3_create_function)
- decimal.js: [Documentation](https://mikemcl.github.io/decimal.js/)
