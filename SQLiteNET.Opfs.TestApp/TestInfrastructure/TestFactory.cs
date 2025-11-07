using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.CRUD;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.JsonCollections;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Migrations;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.RaceConditions;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Relationships;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Transactions;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.TypeMarshalling;
using SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure;

internal class TestFactory
{
    private readonly List<(string Category, SqliteWasmTest Test)> _tests = [];

    public TestFactory(IDbContextFactory<TodoDbContext> factory)
    {
        PopulateTests(factory);
    }

    public IEnumerable<(string Category, SqliteWasmTest Test)> GetTests(string? testName = null)
    {
        var tests = testName is null ? _tests : _tests.Where(t => t.Test.Name == testName);

        var valueTuples = tests as (string Category, SqliteWasmTest Test)[] ?? tests.ToArray();
        return valueTuples.Length > 0 ? valueTuples : Enumerable.Empty<(string Category, SqliteWasmTest Test)>();
    }

    private void PopulateTests(IDbContextFactory<TodoDbContext> factory)
    {
        // Type Marshalling Tests
        _tests.Add(("Type Marshalling", new AllTypesRoundTripTest(factory)));
        _tests.Add(("Type Marshalling", new IntegerTypesBoundariesTest(factory)));
        _tests.Add(("Type Marshalling", new NullableTypesAllNullTest(factory)));
        _tests.Add(("Type Marshalling", new BinaryDataLargeBlobTest(factory)));
        _tests.Add(("Type Marshalling", new StringValueUnicodeTest(factory)));

        // JSON Collection Tests
        _tests.Add(("JSON Collections", new IntListRoundTripTest(factory)));
        _tests.Add(("JSON Collections", new IntListEmptyTest(factory)));
        _tests.Add(("JSON Collections", new IntListLargeCollectionTest(factory)));

        // CRUD Tests
        _tests.Add(("CRUD", new CreateSingleEntityTest(factory)));
        _tests.Add(("CRUD", new ReadByIdTest(factory)));
        _tests.Add(("CRUD", new UpdateModifyPropertyTest(factory)));
        _tests.Add(("CRUD", new DeleteSingleEntityTest(factory)));
        _tests.Add(("CRUD", new BulkInsert100EntitiesTest(factory)));

        // Transaction Tests
        _tests.Add(("Transactions", new TransactionCommitTest(factory)));
        _tests.Add(("Transactions", new TransactionRollbackTest(factory)));

        // Relationship Tests (binary(16) Guid keys + one-to-many)
        _tests.Add(("Relationships", new TodoListCreateWithGuidKeyTest(factory)));
        _tests.Add(("Relationships", new TodoCreateWithForeignKeyTest(factory)));
        _tests.Add(("Relationships", new TodoListIncludeNavigationTest(factory)));
        _tests.Add(("Relationships", new TodoListCascadeDeleteTest(factory)));
        _tests.Add(("Relationships", new TodoComplexQueryWithJoinTest(factory)));
        _tests.Add(("Relationships", new TodoNullableDateTimeTest(factory)));

        // Migration Tests (EF Core migrations in WASM/OPFS)
        _tests.Add(("Migrations", new FreshDatabaseMigrateTest(factory)));
        _tests.Add(("Migrations", new ExistingDatabaseMigrateIdempotentTest(factory)));
        _tests.Add(("Migrations", new MigrationHistoryTableTest(factory)));
        _tests.Add(("Migrations", new GetAppliedMigrationsTest(factory)));
        _tests.Add(("Migrations", new DatabaseExistsCheckTest(factory)));
        _tests.Add(("Migrations", new EnsureCreatedVsMigrateConflictTest(factory)));

        // Race Condition Tests (Concurrency and sync patterns)
        _tests.Add(("Race Conditions", new PurgeThenLoadRaceConditionTest(factory)));
        _tests.Add(("Race Conditions", new PurgeThenLoadWithTransactionTest(factory)));

        // EF Core Functions Tests (ef_ scalar and aggregate functions)
        _tests.Add(("EF Core Functions", new DecimalArithmeticTest(factory)));
        _tests.Add(("EF Core Functions", new DecimalAggregatesTest(factory)));
        _tests.Add(("EF Core Functions", new DecimalComparisonTest(factory)));
        _tests.Add(("EF Core Functions", new RegexPatternTest(factory)));
        _tests.Add(("EF Core Functions", new ComplexDecimalQueryTest(factory)));
    }
}

