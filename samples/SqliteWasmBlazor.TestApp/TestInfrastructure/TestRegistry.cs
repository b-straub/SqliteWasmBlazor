namespace SqliteWasmBlazor.TestApp.TestInfrastructure;

/// <summary>
/// Single source of truth for test-case names. Both <see cref="TestFactory"/>
/// (the TestApp Blazor dispatcher) and <c>SqliteWasmTestBase</c> (the
/// Playwright host xUnit Theory) consume these lists. Adding or removing a
/// test means updating the matching list here and the <c>Add(...)</c> line in
/// <see cref="TestFactory"/>; drift is asserted at TestFactory construction.
///
/// <para>
/// The split is which worker bundle a test needs. <see cref="PlainPlaneNames"/>
/// runs against either — the TestApp boots the plain bundle when the URL says
/// <c>?plane=plain</c>, which is the only way base's own worker cases
/// (<c>replaceDb</c>, the import sessions, the streaming handlers) are ever
/// executed: with the Crypto package registered the bridge points at that
/// bundle instead. <see cref="CryptoPlaneNames"/> needs the encrypted VFS and
/// runs only there.
/// </para>
/// </summary>
public static class TestRegistry
{
    /// <summary>
    /// Tests that need nothing beyond <c>AddSqliteWasm</c>. Run twice: once
    /// against each worker bundle.
    /// </summary>
    public static readonly IReadOnlyList<string> PlainPlaneNames =
    [
        // Type Marshalling
        "AllTypes_RoundTrip",
        "IntegerTypes_Boundaries",
        "NullableTypes_AllNull",
        "BinaryData_LargeBlob",
        "StringValue_Unicode",
        "DateTimeOffset_TextStorage",
        "TimeSpan_Conversion",
        "Char_SingleCharString",
        "Guid_Utf8ByteArray",
        "Guid_HasDataSeedQuery",

        // JSON Collections
        "IntList_RoundTrip",
        "IntList_Empty",
        "IntList_LargeCollection",

        // CRUD
        "Create_SingleEntity",
        "Read_ById",
        "UpdateModifyProperty",
        "Delete_SingleEntity",
        "BulkInsert_100Entities",
        "FTS5_Search",
        "FTS5_SoftDeleteThenClear",
        "SoftDelete_ViaFind",
        "BulkImport_RowsAreEfAddressable",

        // Transactions
        "Transaction_Commit",
        "Transaction_Rollback",

        // Relationships
        "TodoList_CreateWithGuidKey",
        "Todo_CreateWithForeignKey",
        "TodoList_IncludeNavigation",
        "TodoList_CascadeDelete",
        "Todo_ComplexQueryWithJoin",
        "Todo_NullableDateTime",

        // Migrations
        "Migration_FreshDatabaseMigrate",
        "Migration_ExistingDatabaseIdempotent",
        "Migration_HistoryTableTracking",
        "Migration_GetAppliedMigrations",
        "Migration_DatabaseExistsCheck",
        "Migration_EnsureCreatedVsMigrateConflict",
        "Migration_LostHistorySurfacesMismatch",
        "Migration_PopulatedDatabaseUpgrade",
        "Migration_InterruptedUpgradeFailsLoudly",

        // Race Conditions
        "RaceCondition_PurgeThenLoad",
        "RaceCondition_PurgeThenLoadWithTransaction",

        // EF Core Functions
        "EFCoreFunctions_DecimalArithmetic",
        "EFCoreFunctions_DecimalAggregates",
        "EFCoreFunctions_DecimalComparison",
        "EFCoreFunctions_DecimalComparisonSimple",
        "EFCoreFunctions_RegexPattern",
        "EFCoreFunctions_ComplexDecimalQuery",
        "EFCoreFunctions_AggregateBuiltIn",

        // Raw Database Import/Export
        "ExportImport_RawDatabase",
        "ImportRawDatabase_WithBackup",
        "ExportRawDatabase_ReOpenAfterExport",
        "ExportRawDatabase_StagedDownload",
        "ImportRawDatabase_IntoNewDatabase",
        "ImportRawDatabase_IncompatibleSchema",
        "ImportRawDatabase_AutoReOpenAfterImport",
        "ImportRawDatabase_SequentialImports",
        "ImportExportRawDatabase_ImportThenExport",
        "ImportRawDatabase_SchemaValidationExtension",
        "ImportRawDatabase_ReconcilesHostSchema",

        // Checkpoints
        "RestoreToCheckpoint_Basic",
        "RestoreToCheckpoint_WithDeltaReapply",

    ];

    /// <summary>
    /// Tests that need the Crypto worker bundle: the encrypted VFS, or the
    /// forked pool's opaque slot write.
    /// </summary>
    public static readonly IReadOnlyList<string> CryptoPlaneNames =
    [
        "Migration_PopulatedDatabaseUpgradeEncrypted",
        "Migration_BootDefersAndUnlockApplies",
        // Opaque writes — the raw slot write that accepts non-SQLite bytes.
        // Plane 2 forks the pool for it so ciphertext survives import; the
        // vendor pool this plane runs validates the header and refuses, which
        // is right for a pool with no ciphertext in it. These two exercise the
        // corrupt-file recovery flow through that seam.
        "ImportRawDatabase_InvalidFile",
        "ImportRawDatabase_BackupRestoreOnFailure",

        // VFS Encryption
        "VFS_EncryptedRoundTrip",
        "VFS_OnDiskCiphertext",
        "VFS_PlainRegression",
        "VFS_WrongKeyFails",
        "VFS_TamperDetection",
        "VFS_ModeMismatch",
        "VFS_PhysicalLayout",
        "VFS_PerformanceSmoke",
        "VFS_PerformanceSmoke_SameJournalMode",
        "VFS_ManifestMacRejectsWrongKey",
        "VFS_EnterEncrypted_PreExistingPlainDb",

        // VFS Encryption — PRF synthetic-seed compositions
        "PRF_CredentialMismatchSurfacesTypedFailure",
        "Synthetic_PrfSeed_DrivesEncryptedVfsRoundTrip",
        "Synthetic_PrfSeed_EncryptInPlace_PreservesRowsUnderKey",
        "Synthetic_PrfSeed_DecryptInPlace_PreservesRowsAsPlain",
        "SingleDb_StreamingImport_PlainAndEncryptedRoundTrip",
        "SingleDb_StreamingImport_OverOpenDatabase",
        "SingleDb_ValidatedImport_RejectedBySchemaCheck",
        "SingleDb_ValidatedImport_AcceptedOnEncryptedPool",
        "Dbs_ValidatedImport_RejectedBySchemaCheck",
        "Plain_DbsEnvelope_StreamingImport_RoundTrip",
        "PoolImport_GuidedCrossKey_StreamingRoundTrip",
    ];

    /// <summary>Every test, in the order the Crypto-plane run executes them.</summary>
    public static readonly IReadOnlyList<string> AllNames =
        [.. PlainPlaneNames, .. CryptoPlaneNames];

    /// <summary>
    /// xUnit <c>[MemberData]</c> adapter for the full list. Each row is a
    /// single-element <c>object[]</c> consumed by the Playwright <c>[Theory]</c>
    /// in <c>ChromiumTest</c>.
    /// </summary>
    public static IEnumerable<object[]> NamesAsTheoryData =>
        AllNames.Select(name => new object[] { name });

    /// <summary>
    /// Same adapter for the plain-plane subset, consumed by
    /// <c>PlainPlaneTest</c>.
    /// </summary>
    public static IEnumerable<object[]> PlainNamesAsTheoryData =>
        PlainPlaneNames.Select(name => new object[] { name });
}
