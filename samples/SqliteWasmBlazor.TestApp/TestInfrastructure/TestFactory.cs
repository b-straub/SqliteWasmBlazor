using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Checkpoints;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.JsonCollections;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Recovery;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.RaceConditions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Relationships;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;
using SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure;

/// <summary>
/// Wrapper for SqliteWasmTest harness.
/// </summary>
internal record TestEntry(string Category, string Name, Func<ValueTask<string?>> RunAsync);

internal class TestFactory
{
    private readonly List<TestEntry> _entries = [];

    public TestFactory(
        IDbContextFactory<TodoDbContext> todoFactory,
        ISqliteWasmDatabaseService databaseService,
        ICryptoProvider? cryptoProvider = null,
        IDbContextFactory<EncryptedTestContext>? encryptedFactory = null,
        IDbContextFactory<PlainVfsTestContext>? plainVfsFactory = null,
        IServiceProvider? services = null)
    {
        // Encrypted-VFS session — required for any encryption-aware test.
        // Resolved from `services` to avoid threading another constructor
        // arg through every callsite of TestFactory.
        var session = services?.GetService(typeof(IEncryptedSqliteWasmDatabaseService))
            as IEncryptedSqliteWasmDatabaseService;

        PopulateTests(todoFactory, databaseService);
        if (services is not null)
        {
            PopulateMigrationRecoveryTests(services);
        }
        if (encryptedFactory is not null && session is not null)
        {
            PopulateVfsEncryptionTests(encryptedFactory, todoFactory, databaseService, plainVfsFactory, session);
            if (services is not null)
            {
                var prfMismatch = new PrfCredentialMismatchFailureTest(services);
                _entries.Add(new TestEntry("VFS Encryption", prfMismatch.Name, () => prfMismatch.RunTestWithFreshDatabaseAsync()));

                // R3.1 composition test — drives a synthetic PRF seed through
                // ICryptoProvider.StoreKeysAsync → X25519 pubkey-bytes → VFS.
                // Requires the full DI scope (PrfVfsTestContext factory + the
                // crypto provider). Cleans the on-disk DB after itself so the
                // PrfVfsTest demo page sees a fresh OPFS afterwards.
                var prfFactory = services.GetService(
                    typeof(IDbContextFactory<PrfVfsTestContext>))
                    as IDbContextFactory<PrfVfsTestContext>;
                var provider = services.GetService(typeof(ICryptoProvider))
                    as ICryptoProvider;
                if (prfFactory is not null && provider is not null)
                {
                    var syntheticSeed = new SyntheticPrfSeedRoundTripTest(
                        prfFactory, databaseService, provider, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", syntheticSeed.Name, () => syntheticSeed.RunAsync()));

                    // In-place encrypt/decrypt — bytes never cross the
                    // C#↔JS boundary; symmetric pair completing the
                    // plain↔encrypted matrix.
                    var syntheticEncryptInPlace = new SyntheticPrfSeedEncryptInPlaceTest(
                        prfFactory, databaseService, provider, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", syntheticEncryptInPlace.Name, () => syntheticEncryptInPlace.RunAsync()));

                    var syntheticDecryptInPlace = new SyntheticPrfSeedDecryptInPlaceTest(
                        prfFactory, databaseService, provider, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", syntheticDecryptInPlace.Name, () => syntheticDecryptInPlace.RunAsync()));

                    // Encrypted → plain round-trip via LeaveEncrypted +
                    // ExportAll ZIP. The legacy v1 envelope tests were
                    // dropped with the ExportDiskAsync(byte[]) symmetric
                    // overload; the asymmetric round-trip below is the
                    // canonical replacement.
                    var diskExportImport = new DiskExportImportRoundTripTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", diskExportImport.Name, () => diskExportImport.RunAsync()));

                    // Asymmetric ExportDiskToPubkeyAsync + ImportDiskAsync
                    // (v2) round-trip — exercises every cryptographic seam
                    // of the new flow: ECIES wrap toward the seed-derived
                    // X25519 pubkey, ECIES unwrap with the matching priv,
                    // worker rekey-on-import. Synthetic PRF seed simulates
                    // a successful WebAuthn ceremony without virtual
                    // authenticator scaffolding.
                    var keyCache = services.GetService(typeof(ISecureKeyCache))
                        as ISecureKeyCache;
                    var prfService = services.GetService(typeof(IPrfService))
                        as IPrfService;
                    if (keyCache is not null && prfService is not null)
                    {
                        var diskExportToPubkey = new DiskExportToPubkeyRoundTripTest(
                            prfFactory, databaseService, session, provider, keyCache, prfService);
                        _entries.Add(new TestEntry(
                            "VFS Encryption", diskExportToPubkey.Name, () => diskExportToPubkey.RunAsync()));

                        // Cross-key round-trip for ImportDiskGuidedAsync: sender
                        // and recipient use DIFFERENT synthetic seeds, mirroring
                        // the A→B share flow. Validates the guided primitive
                        // does wipe + EnterEncrypted + rekey-import atomically
                        // and rebinds the disk's manifest to the recipient's
                        // credentialId.
                        var diskImportGuided = new DiskImportGuidedCrossKeyTest(
                            prfFactory, databaseService, session, provider, keyCache, prfService);
                        _entries.Add(new TestEntry(
                            "VFS Encryption", diskImportGuided.Name, () => diskImportGuided.RunAsync()));

                        var diskImportGuidedReject = new DiskImportGuidedRejectDoesNotWipeTest(
                            prfFactory, databaseService, session, provider, keyCache, prfService);
                        _entries.Add(new TestEntry(
                            "VFS Encryption", diskImportGuidedReject.Name, () => diskImportGuidedReject.RunAsync()));
                    }

                    // Pure-plain ZIP round-trip — exercises the new
                    // ISqliteWasmDatabaseService.ExportAll/ImportAll batch
                    // primitives on a Plain disk (no encryption involved).
                    var plainBatch = new PlainBatchExportImportRoundTripTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", plainBatch.Name, () => plainBatch.RunAsync()));

                    // Plain ZIP imported onto an Encrypted+Locked disk:
                    // recovery path that drops the manifest + globalKey,
                    // unpacks plain bytes, and ends Plain so the user can
                    // re-encrypt under any new passkey afterwards.
                    var importPlainLocked = new ImportPlainZipFromLockedTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", importPlainLocked.Name, () => importPlainLocked.RunAsync()));

                    // Plain ZIP imported onto an Encrypted+Unlocked disk:
                    // re-encrypts each ZIP entry under the registered
                    // globalKey via the importDbPlain worker handler.
                    // Manifest + passkey binding survive untouched.
                    var importPlainUnlocked = new ImportPlainZipFromUnlockedTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", importPlainUnlocked.Name, () => importPlainUnlocked.RunAsync()));

                    var importPlainBadShape = new ImportPlainZipRejectsBadShapeTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", importPlainBadShape.Name, () => importPlainBadShape.RunAsync()));

                    // Codex audit invariant: manifest MAC verifies on
                    // UnlockAsync — wrong key throws before SQL runs and
                    // leaves the disk in a clean Encrypted+Locked state.
                    var macReject = new VfsManifestMacRejectsWrongKeyTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", macReject.Name, () => macReject.RunAsync()));

                    // Codex audit invariant: EnterEncryptedAsync with a
                    // non-empty pre-existing pool snapshots every plain
                    // DB before encrypt-in-place. Happy-path test — the
                    // rollback branch needs bridge fault-injection,
                    // tracked separately.
                    var preExistingPlain = new VfsEnterEncryptedPreExistingPlainDbTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", preExistingPlain.Name, () => preExistingPlain.RunAsync()));
                }
            }
        }

        AssertRegistryParity();
    }

    /// <summary>
    /// Catches drift between <see cref="TestRegistry.AllNames"/> and the
    /// runtime-registered entries. A registered name not in the registry means
    /// either the registry forgot to declare it (Playwright won't run it) or
    /// the factory typo'd the name (TestApp dispatch will miss it). The reverse
    /// direction (registry name with no factory entry) only fires in the
    /// maximal DI scope, so we don't assert it here to avoid false alarms when
    /// optional services aren't wired.
    /// </summary>
    private void AssertRegistryParity()
    {
        var declared = TestRegistry.AllNames.ToHashSet();
        var unknown = _entries.Select(e => e.Name).Where(n => !declared.Contains(n)).OrderBy(n => n).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"TestFactory registered names not in TestRegistry.AllNames: " +
                $"{string.Join(", ", unknown)}. Add them to TestRegistry.cs or " +
                $"correct the typo in TestFactory.");
        }
    }

    private void PopulateMigrationRecoveryTests(IServiceProvider services)
    {
        const string cat = "Migrations";

        var t1 = new RecoveryHistoryRebuildTest(services);
        _entries.Add(new TestEntry(cat, t1.Name, () => t1.RunTestWithFreshDatabaseAsync()));

        var t2 = new RecoveryDroppedColumnTest(services);
        _entries.Add(new TestEntry(cat, t2.Name, () => t2.RunTestWithFreshDatabaseAsync()));

        var t3 = new RecoveryExtraColumnTest(services);
        _entries.Add(new TestEntry(cat, t3.Name, () => t3.RunTestWithFreshDatabaseAsync()));
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

    private void PopulateVfsEncryptionTests(
        IDbContextFactory<EncryptedTestContext> encryptedFactory,
        IDbContextFactory<TodoDbContext> todoFactory,
        ISqliteWasmDatabaseService databaseService,
        IDbContextFactory<PlainVfsTestContext>? plainVfsFactory,
        IEncryptedSqliteWasmDatabaseService session)
    {
        const string cat = "VFS Encryption";

        var t1 = new VfsEncryptedRoundTripTest(encryptedFactory, databaseService, session);
        _entries.Add(new TestEntry(cat, t1.Name, () => t1.RunTestWithFreshDatabaseAsync()));

        var t2 = new VfsOnDiskCiphertextTest(encryptedFactory, databaseService, session);
        _entries.Add(new TestEntry(cat, t2.Name, () => t2.RunTestWithFreshDatabaseAsync()));

        var t3 = new VfsPlainRegressionTest(todoFactory, databaseService, session);
        _entries.Add(new TestEntry(cat, t3.Name, () => t3.RunTestWithFreshDatabaseAsync()));

        var t4 = new VfsWrongKeyFailsTest(encryptedFactory, databaseService, session);
        _entries.Add(new TestEntry(cat, t4.Name, () => t4.RunTestWithFreshDatabaseAsync()));

        var t5 = new VfsTamperDetectionTest(encryptedFactory, databaseService, session);
        _entries.Add(new TestEntry(cat, t5.Name, () => t5.RunTestWithFreshDatabaseAsync()));

        var t6 = new VfsModeMismatchTest(encryptedFactory, databaseService, session);
        _entries.Add(new TestEntry(cat, t6.Name, () => t6.RunTestWithFreshDatabaseAsync()));

        var t7 = new VfsPhysicalLayoutTest(encryptedFactory, databaseService, session);
        _entries.Add(new TestEntry(cat, t7.Name, () => t7.RunTestWithFreshDatabaseAsync()));

        if (plainVfsFactory is not null)
        {
            var t8 = new VfsEncryptedPerformanceSmokeTest(plainVfsFactory, encryptedFactory, databaseService, session);
            _entries.Add(new TestEntry(cat, t8.Name, () => t8.RunTestWithFreshDatabaseAsync()));

            var t9 = new VfsSameJournalModePerformanceTest(plainVfsFactory, encryptedFactory, databaseService, session);
            _entries.Add(new TestEntry(cat, t9.Name, () => t9.RunTestWithFreshDatabaseAsync()));
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
        Add("CRUD", new ExecuteUpdateDeleteTest(factory));
        Add("CRUD", new RawSqlQueryCommandTest(factory));
        Add("CRUD", new FTS5SearchTest(factory));
        Add("CRUD", new FTS5SoftDeleteThenClearTest(factory));

        // ADO.NET Tests
        Add("ADO.NET", new AsyncCommandRoundTripTest(factory));
        Add("ADO.NET", new CommandBehaviorCloseConnectionTest(factory));
        Add("ADO.NET", new CommandBehaviorReaderOptionsTest(factory));
        Add("ADO.NET", new CommandTypeValidationTest(factory));
        Add("ADO.NET", new ConnectionStringParsingTest(factory));
        Add("ADO.NET", new ConstraintConflictHandlingTest(factory));
        Add("ADO.NET", new ParameterDbTypeBindingTest(factory));
        Add("ADO.NET", new ParameterDateOnlyTimeOnlyTest(factory));
        Add("ADO.NET", new ReaderDateOnlyTimeOnlyTest(factory));
        Add("ADO.NET", new EmptyReaderMetadataTest(factory));
        Add("ADO.NET", new NativeAggregateWindowFunctionsTest(factory));
        Add("ADO.NET", new NativeDateTimeFunctionsTest(factory));
        Add("ADO.NET", new NativeFts5FunctionsTest(factory));
        Add("ADO.NET", new NativeJsonFunctionsTest(factory));
        Add("ADO.NET", new NativePragmaIntrospectionTest(factory));
        Add("ADO.NET", new NativePragmaSettingsTest(factory));
        Add("ADO.NET", new NativeScalarFunctionsTest(factory));
        Add("ADO.NET", new NativeStateFunctionsTest(factory));
        Add("ADO.NET", new NativeVirtualTableModulesTest(factory));
        Add("ADO.NET", new ParameterPrefixCompatibilityTest(factory));
        Add("ADO.NET", new ReaderOrdinalCompatibilityTest(factory));
        Add("ADO.NET", new ReaderRecordsAffectedTest(factory));
        Add("ADO.NET", new ReaderSchemaTableTest(factory));
        Add("ADO.NET", new ReaderStreamingAccessTest(factory));
        Add("ADO.NET", new ReaderTypedFieldValuesTest(factory));
        Add("ADO.NET", new ReturningClausesTest(factory));
        Add("ADO.NET", new RowsAffectedStatementPrefixesTest(factory));
        Add("ADO.NET", new SyncCommandApisThrowTest(factory));

        // Transaction Tests
        Add("Transactions", new TransactionCommitTest(factory));
        Add("Transactions", new TransactionRollbackTest(factory));
        Add("Transactions", new TransactionDisposeRollsBackTest(factory));
        Add("Transactions", new TransactionConcurrentBeginSerializesTest(factory));
        Add("Transactions", new TransactionBlocksIndependentCommandTest(factory));
        Add("Transactions", new TransactionIsolationLevelTest(factory));
        Add("Transactions", new TransactionSavepointTest(factory));

        // Relationship Tests (binary(16) Guid keys + one-to-many)
        Add("Relationships", new TodoListCreateWithGuidKeyTest(factory));
        Add("Relationships", new TodoCreateWithForeignKeyTest(factory));
        Add("Relationships", new TodoListIncludeNavigationTest(factory));
        Add("Relationships", new TodoListCascadeDeleteTest(factory));
        Add("Relationships", new TodoComplexQueryWithJoinTest(factory));
        Add("Relationships", new TodoNullableDateTimeTest(factory));
        Add("Relationships", new ForeignKeyEnforcementTest(factory));

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
        Add("EF Core Functions", new ConversionTranslationsTest(factory));
        Add("EF Core Functions", new CommonQueryTranslationsTest(factory));
        Add("EF Core Functions", new StringAdvancedTranslationsTest(factory));
        Add("EF Core Functions", new CollectionPredicateTranslationsTest(factory));
        Add("EF Core Functions", new TypedPredicateTranslationsTest(factory));
        Add("EF Core Functions", new DateTimeTranslationsTest(factory));
        Add("EF Core Functions", new DateOnlyTimeOnlyParameterTest(factory));
        Add("EF Core Functions", new GroupedAggregateSetOperationsTest(factory));
        Add("EF Core Functions", new MathTranslationsTest(factory));
        Add("EF Core Functions", new SqliteSpecificDbFunctionsTest(factory));
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
