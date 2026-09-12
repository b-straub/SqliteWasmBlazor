using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Cancellation;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Checkpoints;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.JsonCollections;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;
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

        PopulateTests(todoFactory, databaseService, cryptoPlane: session is not null);
        if (services?.GetService(typeof(TestHostDatabaseService)) is TestHostDatabaseService host)
        {
            var reconcile = new ImportReconcilesHostSchemaTest(todoFactory, databaseService, host);
            _entries.Add(new TestEntry(
                "Import/Export", reconcile.Name, () => reconcile.RunTestWithFreshDatabaseAsync()));
        }
        if (services is not null)
        {
            PopulateMigrationUpgradeTests(services);
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

                    // Single-DB streaming import round-trip — covers the
                    // IBrowserFile path the page uses (Plain + Unlocked
                    // dispatch). Plain → Plain round-trip + Plain →
                    // Encrypted+Unlocked rekey-on-write in one run.
                    var singleDbStreaming = new SingleDbStreamingRoundTripTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", singleDbStreaming.Name, () => singleDbStreaming.RunAsync()));

                    // Same path, but the target is OPEN when the import
                    // commits — atomicReplaceFile frees the SAH an OFile
                    // captured at xOpen, so the import has to close first
                    // or every later query is served from a freed slot.
                    var singleDbOverOpen = new SingleDbImportOverOpenDatabaseTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", singleDbOverOpen.Name, () => singleDbOverOpen.RunAsync()));

                    // Validated import whose validator refuses the file —
                    // the wrong-row pick. The database has to come out of it
                    // with its own rows and no park left behind.
                    var singleDbRejected = new SingleDbValidatedImportRejectedTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", singleDbRejected.Name, () => singleDbRejected.RunAsync()));

                    // The accepted half, on an encrypted pool: page AAD binds
                    // ciphertext to the database path, so the import writes
                    // under the real name and parks what was there. The
                    // validator's own read is what proves it.
                    var singleDbAccepted = new SingleDbValidatedImportAcceptedTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", singleDbAccepted.Name, () => singleDbAccepted.RunAsync()));

                    // Same gate on the bundle, where a wrong file replaces
                    // every database at once.
                    var dbsRejected = new DbsValidatedImportRejectedTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", dbsRejected.Name, () => dbsRejected.RunAsync()));

                    // Streaming cross-key guided import — the .eds rebind
                    // path. Plants the recipient X25519 keypair under the PRF
                    // convention id (needs IPrfService for the salt), exports
                    // a real envelope via the bytes seam, then guided-imports
                    // it under a different vfsKey + credential. Closes the
                    // browser-E2E gap from c239c23.
                    var prfService = services.GetService(typeof(IPrfService)) as IPrfService;
                    if (prfService is not null)
                    {
                        var guidedCrossKey = new PoolImportGuidedCrossKeyStreamingTest(
                            prfFactory, databaseService, provider, prfService, session);
                        _entries.Add(new TestEntry(
                            "VFS Encryption", guidedCrossKey.Name, () => guidedCrossKey.RunAsync()));
                    }

                    // Multi-DB .dbs envelope streaming import round-trip
                    // — the page emits this envelope from the checkbox
                    // export selector when ≥ 2 DBs are picked; the import
                    // side wipes the pool then writes each MessagePack
                    // entry through the chunked SAH path.
                    var dbsEnvelope = new DbsEnvelopeRoundTripTest(
                        prfFactory, databaseService, session);
                    _entries.Add(new TestEntry(
                        "VFS Encryption", dbsEnvelope.Name, () => dbsEnvelope.RunAsync()));

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

        AssertRegistryParity(cryptoPlane: session is not null);
    }

    /// <summary>
    /// Catches drift between <see cref="TestRegistry"/> and the
    /// runtime-registered entries. A registered name not in the registry means
    /// either the registry forgot to declare it (Playwright won't run it) or
    /// the factory typo'd the name (TestApp dispatch will miss it).
    ///
    /// <para>
    /// On the plain plane the check runs both ways. That run has no optional
    /// services to make a missing entry ambiguous — everything
    /// <see cref="TestRegistry.PlainPlaneNames"/> declares must be registered,
    /// and nothing else — so a name the plain Playwright theory would wait for
    /// and never see is caught here instead.
    /// </para>
    /// </summary>
    /// <param name="cryptoPlane">
    /// <c>false</c> when the Crypto services are absent, which is the
    /// <c>?plane=plain</c> run.
    /// </param>
    private void AssertRegistryParity(bool cryptoPlane)
    {
        var registered = _entries.Select(e => e.Name).ToHashSet();
        var declared = (cryptoPlane ? TestRegistry.AllNames : TestRegistry.PlainPlaneNames).ToHashSet();

        var unknown = registered.Where(n => !declared.Contains(n)).OrderBy(n => n).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"TestFactory registered names not declared in TestRegistry: " +
                $"{string.Join(", ", unknown)}. Add them to TestRegistry.cs or " +
                $"correct the typo in TestFactory.");
        }

        if (cryptoPlane)
        {
            // The Crypto run can legitimately be missing entries when an
            // optional service is unwired, so the reverse direction would
            // false-alarm.
            return;
        }

        var missing = declared.Where(n => !registered.Contains(n)).OrderBy(n => n).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"TestRegistry.PlainPlaneNames declares tests the plain-plane run does " +
                $"not register: {string.Join(", ", missing)}. Either the test needs the " +
                $"Crypto worker (move it to CryptoPlaneNames) or its TestFactory entry " +
                $"is behind a condition the plain run does not meet.");
        }
    }

    private void PopulateMigrationUpgradeTests(IServiceProvider services)
    {
        const string cat = "Migrations";

        var t1 = new LostHistoryTest(services);
        _entries.Add(new TestEntry(cat, t1.Name, () => t1.RunTestWithFreshDatabaseAsync()));

        // Upgrade cases own MigrationProbeDb.db, not TodoDb.db, so they manage
        // their own lifecycle rather than going through SqliteWasmTest.
        var u1 = new PopulatedUpgradeTest(services);
        _entries.Add(new TestEntry(cat, u1.Name, () => u1.RunTestWithFreshDatabaseAsync()));

        var u2 = new InterruptedUpgradeTest(services);
        _entries.Add(new TestEntry(cat, u2.Name, () => u2.RunTestWithFreshDatabaseAsync()));

        // Encrypted pool: the same upgrade with the worker-wide key installed.
        // Only registered where the encrypted VFS exists.
        if (services.GetService<IEncryptedSqliteWasmDatabaseService>() is not null)
        {
            var u3 = new EncryptedPopulatedUpgradeTest(services);
            _entries.Add(new TestEntry(cat, u3.Name, () => u3.RunTestWithFreshDatabaseAsync()));

            // Where the migration runs, not what it costs: boot registers the
            // work and the first render applies it — on a locked pool, the
            // unlock does.
            var u4 = new BootDeferredMigrationTest(
                services,
                services.GetRequiredService<ISqliteWasmDatabaseService>(),
                services.GetRequiredService<IEncryptedSqliteWasmDatabaseService>());
            _entries.Add(new TestEntry(cat, u4.Name, () => u4.RunTestWithFreshDatabaseAsync()));
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

    private void PopulateTests(
        IDbContextFactory<TodoDbContext> factory,
        ISqliteWasmDatabaseService databaseService,
        bool cryptoPlane)
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
        Add("CRUD", new SoftDeleteViaFindTest(factory));
        Add("CRUD", new BulkImportedRowsAreEfAddressableTest(factory, databaseService));

        // Transaction Tests
        Add("Transactions", new TransactionCommitTest(factory));
        Add("Transactions", new TransactionRollbackTest(factory));

        // Cancellation — one case per service-worker situation; see TestRegistry.
        Add("Cancellation", new CancellationInterruptsRunningStatementTest(factory, databaseService));
        Add("Cancellation", new CancellationUnavailableWithoutServiceWorkerTest(factory, databaseService));

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
        if (cryptoPlane)
        {
            // Both write non-SQLite bytes through the raw slot seam. Only the
            // forked pool in SqliteWasmBlazor.Crypto accepts those — the
            // vendor pool this plane runs validates the header, which is
            // correct for a pool that never holds ciphertext.
            Add("Import/Export", new RawDatabaseImportInvalidFileTest(factory, databaseService));
            Add("Import/Export", new RawDatabaseBackupRestoreOnFailureTest(factory, databaseService));
        }

        Add("Import/Export", new RawDatabaseImportWithBackupTest(factory, databaseService));

        Add("Import/Export", new RawDatabaseExportReOpenTest(factory, databaseService));
        Add("Import/Export", new RawDatabaseExportToDownloadTest(factory, databaseService));
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
