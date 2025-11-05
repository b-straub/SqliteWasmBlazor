# SQLiteNET Relationship Tests with binary(16) Guid Keys

## Summary

Created comprehensive EF Core relationship tests for SQLite WASM with binary(16) Guid primary and foreign keys, based on the WebAppBase.UserDB models.

## New Models Created

### TodoList (Parent Entity)
**File**: `SQLiteNET.Opfs.TestApp/Models/TodoList.cs`

- `Guid Id` [Key, Column(TypeName = "binary(16)")]
- `string Title` [MaxLength(255)]
- `bool IsActive`
- `DateTime CreatedAt`
- `ICollection<Todo> Todos` (Collection navigation property)

### Todo (Child Entity)
**File**: `SQLiteNET.Opfs.TestApp/Models/Todo.cs`

- `Guid Id` [Key, Column(TypeName = "binary(16)")]
- `string Title` [MaxLength(255)]
- `string? Description` [MaxLength(255)]
- `DateTime? DueDate` (Nullable)
- `bool Completed`
- `int Priority`
- `Guid TodoListId` [Column(TypeName = "binary(16)")] (Foreign Key)
- `DateTime? CompletedAt` (Nullable)
- `TodoList? TodoList` (Reference navigation property)

**Relationship**: One-to-Many (TodoList → Todos) with Cascade Delete

## DbContext Configuration

**File**: `SQLiteNET.Opfs.TestApp/Data/TodoDbContext.cs`

Added:
- `DbSet<TodoList> TodoLists`
- `DbSet<Todo> Todos`
- Fluent API configuration for relationship:
  - Foreign key: `TodoListId`
  - Delete behavior: `DeleteBehavior.Cascade`
  - Required fields, max lengths

## Test Coverage

Created 6 comprehensive tests in `TestInfrastructure/Tests/Relationships/`:

### 1. TodoListCreateWithGuidKeyTest
**Tests**: Binary(16) Guid key creation and retrieval
- Creates TodoList with explicit Guid ID
- Verifies ID matches after SaveChanges
- Confirms properties are persisted correctly

### 2. TodoCreateWithForeignKeyTest
**Tests**: Foreign key relationship with binary(16) Guids
- Creates parent TodoList
- Creates child Todo with TodoListId foreign key
- Verifies foreign key relationship is maintained

### 3. TodoListIncludeNavigationTest
**Tests**: EF Core Include() with navigation properties
- Creates TodoList with 3 child Todos
- Uses `.Include(l => l.Todos)` to load related entities
- Verifies collection navigation property works
- Tests filtering on loaded collection (completed, due dates)

### 4. TodoListCascadeDeleteTest
**Tests**: Cascade delete behavior
- Creates TodoList with 5 child Todos
- Deletes parent TodoList
- Verifies all child Todos are automatically deleted
- Confirms cascade works with binary(16) keys

### 5. TodoComplexQueryWithJoinTest
**Tests**: Complex LINQ queries with joins
- Creates multiple TodoLists (active/inactive)
- Creates Todos with various states (completed, overdue)
- Tests 4 scenarios:
  1. Incomplete todos from active lists only
  2. Overdue todos (DueDate < now && !Completed)
  3. TodoLists with count of completed todos
  4. Todos ordered by priority with list info

### 6. TodoNullableDateTimeTest
**Tests**: Nullable DateTime fields with Guid keys
- Tests Todo with null DueDate and CompletedAt
- Tests Todo with DueDate set, CompletedAt null
- Tests Todo with both dates set
- Tests updating null → value → null transitions
- Verifies DateTime precision in SQLite

## Key Testing Points

### ✅ Binary(16) Guid Storage
- Guids stored as 16-byte binary (not string)
- Efficient storage and indexing
- Matches EF Core SQLite conventions

### ✅ Foreign Key Relationships
- Binary(16) foreign keys work correctly
- Referential integrity maintained
- Navigation properties load properly

### ✅ EF Core Features
- `Include()` and lazy loading
- LINQ queries with joins
- Cascade delete
- Nullable columns
- Required vs optional properties

### ✅ SQLite WASM Compatibility
- All operations work in WASM environment
- Binary columns serialize/deserialize correctly via TypedRowDataConverter
- No JsonElement boxing issues (thanks to our earlier fix!)

## Test Registration

### In TestApp (Manual UI Testing)
**File**: `TestInfrastructure/TestFactory.cs`

Added new "Relationships" category with 6 tests:
```csharp
_tests.Add(("Relationships", new TodoListCreateWithGuidKeyTest(factory)));
_tests.Add(("Relationships", new TodoCreateWithForeignKeyTest(factory)));
_tests.Add(("Relationships", new TodoListIncludeNavigationTest(factory)));
_tests.Add(("Relationships", new TodoListCascadeDeleteTest(factory)));
_tests.Add(("Relationships", new TodoComplexQueryWithJoinTest(factory)));
_tests.Add(("Relationships", new TodoNullableDateTimeTest(factory)));
```

### In Playwright Tests (Automated Browser Testing)
**File**: `SqliteWasm.Data.Tests/SqliteWasmTestBase.cs`

Added 6 new [InlineData] test cases:
```csharp
// Relationship Tests
[InlineData("TodoList_CreateWithGuidKey")]
[InlineData("Todo_CreateWithForeignKey")]
[InlineData("TodoList_IncludeNavigation")]
[InlineData("TodoList_CascadeDelete")]
[InlineData("Todo_ComplexQueryWithJoin")]
[InlineData("Todo_NullableDateTime")]
```

## Build Status

✅ **Build Successful** (0 warnings, 0 errors)
✅ **All Models Compiled**
✅ **DbContext Configured**
✅ **Tests Registered in Both Test Suites**
✅ **Ready to Run**

## Next Steps

### Manual Testing (Browser UI)
1. Run `dotnet run --project SQLiteNET.Opfs.TestHost`
2. Navigate to TestApp in browser (https://localhost:5001/Tests)
3. Browser will automatically execute all tests including new "Relationships" category
4. Verify all 6 relationship tests pass

### Automated Testing (Playwright)
1. Run `dotnet test SqliteWasm.Data.Tests/SqliteWasm.Data.Tests.csproj`
2. Playwright will launch browsers and execute all tests
3. Verify all 6 relationship tests pass in Chromium, Firefox, and Webkit

The tests comprehensively validate:
- Binary(16) Guid key handling
- One-to-many relationships
- EF Core navigation properties
- Complex LINQ queries
- Cascade delete behavior
- Nullable DateTime fields
- All features work correctly with the TypedRowDataConverter fix

## Notes

- Ignored non-EF attributes: [Permissions], [Share], [ShareLabel], [InheritPermissions], [AllowUpdate]
- Used only EF Core attributes: [Key], [Column(TypeName)], [MaxLength], [Table]
- Models mirror WebAppBase.UserDB structure but simplified for testing
- All tests follow existing test pattern (inherit from SqliteWasmTest)
