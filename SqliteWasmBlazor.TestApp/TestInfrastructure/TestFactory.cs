using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Checkpoints;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Encryption;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.JsonCollections;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.RaceConditions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Relationships;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure;

internal class TestFactory
{
    private readonly List<(string Category, SqliteWasmTest Test)> _tests = [];

    public TestFactory(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    {
        PopulateTests(factory, databaseService);
    }

    public IEnumerable<(string Category, SqliteWasmTest Test)> GetTests(string? testName = null)
    {
        var tests = testName is null ? _tests : _tests.Where(t => t.Test.Name == testName);

        var valueTuples = tests as (string Category, SqliteWasmTest Test)[] ?? tests.ToArray();
        return valueTuples.Length > 0 ? valueTuples : Enumerable.Empty<(string Category, SqliteWasmTest Test)>();
    }

    private void PopulateTests(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    {
        // Type Marshalling Tests
        _tests.Add(("Type Marshalling", new AllTypesRoundTripTest(factory)));
        _tests.Add(("Type Marshalling", new IntegerTypesBoundariesTest(factory)));
        _tests.Add(("Type Marshalling", new NullableTypesAllNullTest(factory)));
        _tests.Add(("Type Marshalling", new BinaryDataLargeBlobTest(factory)));
        _tests.Add(("Type Marshalling", new StringValueUnicodeTest(factory)));

        // Type Conversion Tests (EF Core compatibility fixes)
        _tests.Add(("Type Marshalling", new DateTimeOffsetTextStorageTest(factory)));
        _tests.Add(("Type Marshalling", new TimeSpanConversionTest(factory)));
        _tests.Add(("Type Marshalling", new CharSingleCharStringTest(factory)));
        _tests.Add(("Type Marshalling", new GuidUtf8ByteArrayTest(factory)));

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
        _tests.Add(("CRUD", new FTS5SearchTest(factory)));
        _tests.Add(("CRUD", new FTS5SoftDeleteThenClearTest(factory)));

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
        _tests.Add(("EF Core Functions", new DecimalComparisonSimpleTest(factory)));
        _tests.Add(("EF Core Functions", new RegexPatternTest(factory)));
        _tests.Add(("EF Core Functions", new ComplexDecimalQueryTest(factory)));
        _tests.Add(("EF Core Functions", new AggregateBuiltInTest(factory)));

        // Raw Database Import/Export Tests
        _tests.Add(("Import/Export", new RawDatabaseExportImportTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseImportInvalidFileTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseImportWithBackupTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseBackupRestoreOnFailureTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseExportReOpenTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseImportIntoNewTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseImportIncompatibleSchemaTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseAutoReOpenAfterImportTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseSequentialImportTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseImportThenExportTest(factory, databaseService)));
        _tests.Add(("Import/Export", new RawDatabaseSchemaValidationTest(factory, databaseService)));

        // Checkpoint Tests (rollback and restore functionality)
        _tests.Add(("Checkpoints", new RestoreToCheckpointBasicTest(factory)));
        _tests.Add(("Checkpoints", new RestoreToCheckpointWithDeltaReapplyTest(factory)));

        // Encryption Tests
        _tests.Add(("Encryption", new EncryptionBasicCRUDTest(factory, databaseService)));
        _tests.Add(("Encryption", new EncryptionSpecialCharsPasswordTest(factory, databaseService)));
        _tests.Add(("Encryption", new EncryptionWrongPasswordTest(factory, databaseService)));

        // V2 Bulk Import/Export Tests (worker-side prepared statement loop)
        _tests.Add(("V2 Bulk", new V2BulkTodoRoundTripTest(factory, databaseService)));
        _tests.Add(("V2 Bulk", new V2BulkAllTypesRoundTripTest(factory, databaseService)));
        _tests.Add(("V2 Bulk", new V2BulkNullableAllNullTest(factory, databaseService)));
        _tests.Add(("V2 Bulk", new V2BulkConflictLastWriteWinsTest(factory, databaseService)));
        _tests.Add(("V2 Bulk", new V2BulkConflictLocalWinsTest(factory, databaseService)));
        _tests.Add(("V2 Bulk", new V2BulkConflictDeltaWinsTest(factory, databaseService)));
        _tests.Add(("V2 Bulk Raw", new V2BulkRawImportTest(factory, databaseService)));
        _tests.Add(("V2 Bulk Raw", new V2BulkRawImportConflictTest(factory, databaseService)));
    }
}

