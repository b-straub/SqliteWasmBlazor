using BlazorPRF.Crypto.Abstractions.Services;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Checkpoints;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.JsonCollections;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.RaceConditions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Relationships;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure;

/// <summary>
/// Wrapper for both SqliteWasmTest and CryptoSyncTestBase.
/// </summary>
internal record TestEntry(string Category, string Name, Func<ValueTask<string?>> RunAsync);

internal class TestFactory
{
    private readonly List<TestEntry> _entries = [];

    public TestFactory(
        IDbContextFactory<TodoDbContext> todoFactory,
        ISqliteWasmDatabaseService databaseService,
        IDbContextFactory<CryptoTestContext>? cryptoFactory = null,
        IGroupEncryption? groupEncryption = null)
    {
        PopulateTests(todoFactory, databaseService);
        if (cryptoFactory is not null)
        {
            PopulateCryptoTests(cryptoFactory, databaseService, groupEncryption);
        }
    }

    public IEnumerable<TestEntry> GetTests(string? testName = null, string? category = null)
    {
        IEnumerable<TestEntry> tests = _entries;

        if (testName is not null)
        {
            tests = tests.Where(t => t.Name == testName);
        }
        else if (category is not null)
        {
            tests = tests.Where(t => t.Category == category);
        }

        return tests;
    }

    private void Add(string category, SqliteWasmTest test)
    {
        _entries.Add(new TestEntry(category, test.Name, () => test.RunTestWithFreshDatabaseAsync()));
    }

    private void PopulateCryptoTests(
        IDbContextFactory<CryptoTestContext> cryptoFactory,
        ISqliteWasmDatabaseService databaseService,
        IGroupEncryption? groupEncryption)
    {
        var test1 = new CryptoSyncRoundTripTest(cryptoFactory, databaseService);
        _entries.Add(new TestEntry("Encrypted Delta", test1.Name, () => test1.RunTestWithFreshDatabaseAsync()));

        var test2 = new WorkerEncryptedRoundTripTest(cryptoFactory, databaseService);
        _entries.Add(new TestEntry("Encrypted Delta", test2.Name, () => test2.RunTestWithFreshDatabaseAsync()));

        var test3 = new PermissionEnforcementTest(cryptoFactory, databaseService);
        _entries.Add(new TestEntry("Encrypted Delta", test3.Name, () => test3.RunTestWithFreshDatabaseAsync()));

        var test4 = new SchemaVersionMismatchTest(cryptoFactory, databaseService);
        _entries.Add(new TestEntry("Encrypted Delta", test4.Name, () => test4.RunTestWithFreshDatabaseAsync()));

        var test5 = new MultiTableRoundTripTest(cryptoFactory, databaseService);
        _entries.Add(new TestEntry("Encrypted Delta", test5.Name, () => test5.RunTestWithFreshDatabaseAsync()));

        // SharingAndRotate requires IGroupEncryption — skip gracefully when
        // the caller didn't wire it (older razor pages).
        if (groupEncryption is not null)
        {
            var test6 = new SharingAndRotateTest(cryptoFactory, databaseService, groupEncryption);
            _entries.Add(new TestEntry("Encrypted Delta", test6.Name, () => test6.RunTestWithFreshDatabaseAsync()));
        }
    }

    private void PopulateTests(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    {
        // Type Marshalling Tests
        Add("Type Marshalling", new AllTypesRoundTripTest(factory));
        Add("Type Marshalling", new IntegerTypesBoundariesTest(factory));
        Add("Type Marshalling", new NullableTypesAllNullTest(factory));
        Add("Type Marshalling", new BinaryDataLargeBlobTest(factory));
        Add("Type Marshalling", new StringValueUnicodeTest(factory));

        // Type Conversion Tests (EF Core compatibility fixes)
        Add("Type Marshalling", new DateTimeOffsetTextStorageTest(factory));
        Add("Type Marshalling", new TimeSpanConversionTest(factory));
        Add("Type Marshalling", new CharSingleCharStringTest(factory));
        Add("Type Marshalling", new GuidUtf8ByteArrayTest(factory));
        Add("Type Marshalling", new GuidHasDataSeedQueryTest(factory));

        // JSON Collection Tests
        Add("JSON Collections", new IntListRoundTripTest(factory));
        Add("JSON Collections", new IntListEmptyTest(factory));
        Add("JSON Collections", new IntListLargeCollectionTest(factory));

        // CRUD Tests
        Add("CRUD", new CreateSingleEntityTest(factory));
        Add("CRUD", new ReadByIdTest(factory));
        Add("CRUD", new UpdateModifyPropertyTest(factory));
        Add("CRUD", new DeleteSingleEntityTest(factory));
        Add("CRUD", new BulkInsert100EntitiesTest(factory));
        Add("CRUD", new FTS5SearchTest(factory));
        Add("CRUD", new FTS5SoftDeleteThenClearTest(factory));

        // Transaction Tests
        Add("Transactions", new TransactionCommitTest(factory));
        Add("Transactions", new TransactionRollbackTest(factory));

        // Relationship Tests (binary(16) Guid keys + one-to-many)
        Add("Relationships", new TodoListCreateWithGuidKeyTest(factory));
        Add("Relationships", new TodoCreateWithForeignKeyTest(factory));
        Add("Relationships", new TodoListIncludeNavigationTest(factory));
        Add("Relationships", new TodoListCascadeDeleteTest(factory));
        Add("Relationships", new TodoComplexQueryWithJoinTest(factory));
        Add("Relationships", new TodoNullableDateTimeTest(factory));

        // Migration Tests (EF Core migrations in WASM/OPFS)
        Add("Migrations", new FreshDatabaseMigrateTest(factory));
        Add("Migrations", new ExistingDatabaseMigrateIdempotentTest(factory));
        Add("Migrations", new MigrationHistoryTableTest(factory));
        Add("Migrations", new GetAppliedMigrationsTest(factory));
        Add("Migrations", new DatabaseExistsCheckTest(factory));
        Add("Migrations", new EnsureCreatedVsMigrateConflictTest(factory));

        // Race Condition Tests (Concurrency and sync patterns)
        Add("Race Conditions", new PurgeThenLoadRaceConditionTest(factory));
        Add("Race Conditions", new PurgeThenLoadWithTransactionTest(factory));

        // EF Core Functions Tests (ef_ scalar and aggregate functions)
        Add("EF Core Functions", new DecimalArithmeticTest(factory));
        Add("EF Core Functions", new DecimalAggregatesTest(factory));
        Add("EF Core Functions", new DecimalComparisonTest(factory));
        Add("EF Core Functions", new DecimalComparisonSimpleTest(factory));
        Add("EF Core Functions", new RegexPatternTest(factory));
        Add("EF Core Functions", new ComplexDecimalQueryTest(factory));
        Add("EF Core Functions", new AggregateBuiltInTest(factory));

        // Raw Database Import/Export Tests
        Add("Import/Export", new RawDatabaseExportImportTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseImportInvalidFileTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseImportWithBackupTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseBackupRestoreOnFailureTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseExportReOpenTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseImportIntoNewTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseImportIncompatibleSchemaTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseAutoReOpenAfterImportTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseSequentialImportTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseImportThenExportTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseSchemaValidationTest(factory, databaseService));

        // Checkpoint Tests (rollback and restore functionality)
        Add("Checkpoints", new RestoreToCheckpointBasicTest(factory));
        Add("Checkpoints", new RestoreToCheckpointWithDeltaReapplyTest(factory));

        // V2 Bulk tests removed — all delta sync now goes through encrypted V2 path
    }
}

