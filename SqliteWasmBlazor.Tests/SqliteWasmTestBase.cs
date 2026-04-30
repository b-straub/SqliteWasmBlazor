using Microsoft.Playwright;
using SqliteWasmBlazor.Tests.Infrastructure;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests;

public abstract class SqliteWasmTestBase(IWaFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly IWaFixture _fixture = fixture;
    protected readonly ITestOutputHelper Output = output;

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    [Theory]
    // Type Marshalling Tests
    [InlineData("AllTypes_RoundTrip")]
    [InlineData("IntegerTypes_Boundaries")]
    [InlineData("NullableTypes_AllNull")]
    [InlineData("BinaryData_LargeBlob")]
    [InlineData("StringValue_Unicode")]
    [InlineData("DateTimeOffset_TextStorage")]
    [InlineData("TimeSpan_Conversion")]
    [InlineData("Char_SingleCharString")]
    [InlineData("Guid_Utf8ByteArray")]
    // JSON Collections Tests
    [InlineData("IntList_RoundTrip")]
    [InlineData("IntList_Empty")]
    [InlineData("IntList_LargeCollection")]
    // CRUD Tests
    [InlineData("Create_SingleEntity")]
    [InlineData("Read_ById")]
    [InlineData("UpdateModifyProperty")]
    [InlineData("Delete_SingleEntity")]
    [InlineData("BulkInsert_100Entities")]
    [InlineData("FTS5_Search")]
    [InlineData("FTS5_SoftDeleteThenClear")]
    // Transaction Tests
    [InlineData("Transaction_Commit")]
    [InlineData("Transaction_Rollback")]
    // Relationship Tests
    [InlineData("TodoList_CreateWithGuidKey")]
    [InlineData("Todo_CreateWithForeignKey")]
    [InlineData("TodoList_IncludeNavigation")]
    [InlineData("TodoList_CascadeDelete")]
    [InlineData("Todo_ComplexQueryWithJoin")]
    [InlineData("Todo_NullableDateTime")]
    // Migration Tests
    [InlineData("Migration_FreshDatabaseMigrate")]
    [InlineData("Migration_ExistingDatabaseIdempotent")]
    [InlineData("Migration_HistoryTableTracking")]
    [InlineData("Migration_GetAppliedMigrations")]
    [InlineData("Migration_DatabaseExistsCheck")]
    [InlineData("Migration_EnsureCreatedVsMigrateConflict")]
    // Race Condition Tests
    [InlineData("RaceCondition_PurgeThenLoad")]
    [InlineData("RaceCondition_PurgeThenLoadWithTransaction")]
    // EF Core Functions Tests
    [InlineData("EFCoreFunctions_DecimalArithmetic")]
    [InlineData("EFCoreFunctions_DecimalAggregates")]
    [InlineData("EFCoreFunctions_DecimalComparison")]
    [InlineData("EFCoreFunctions_DecimalComparisonSimple")]
    [InlineData("EFCoreFunctions_RegexPattern")]
    [InlineData("EFCoreFunctions_ComplexDecimalQuery")]
    [InlineData("EFCoreFunctions_AggregateBuiltIn")]
    // Import/Export Tests
    [InlineData("ExportImport_RoundTrip")]
    [InlineData("ExportImport_LargeDataset")]
    [InlineData("ImportIncompatibleSchemaHash")]
    [InlineData("ImportIncompatibleAppId")]
    [InlineData("ExportImport_EmptyDatabase")]
    [InlineData("ExportImport_IncrementalBatches")]
    [InlineData("ExportImport_DeltaBasic")]
    [InlineData("ExportImport_DeltaConflict")]
    [InlineData("ExportImport_DeltaConflict_LocalWins")]
    [InlineData("ExportImport_DeltaConflict_DeltaWins")]
    [InlineData("ExportImport_DeltaDeletion")]
    // CryptoSync encrypted delta tests
    [InlineData("CryptoSync_RoundTrip")]
    [InlineData("CryptoSync_WorkerEncryptedRoundTrip")]
    [InlineData("CryptoSync_PermissionEnforcement")]
    [InlineData("CryptoSync_SchemaVersionMismatch")]
    [InlineData("CryptoSync_MultiTableRoundTrip")]
    // Raw Database Import/Export Tests
    [InlineData("ExportImport_RawDatabase")]
    [InlineData("ImportRawDatabase_InvalidFile")]
    // Checkpoint Tests
    [InlineData("RestoreToCheckpoint_Basic")]
    [InlineData("RestoreToCheckpoint_WithDeltaReapply")]
    public async Task TestCaseAsync(string name)
    {
        Assert.NotNull(_fixture.Page);

        // OnePass mode: one shared page load runs every test sequentially and
        // emits a per-test result label. Each xUnit test polls for its own
        // label. The wait must cover the *cumulative* test queue, not just the
        // single-test runtime — otherwise the xUnit tests scheduled later in
        // the queue time out before the page reaches them.
        var timeout = _fixture.Type switch
        {
            IWaFixture.BrowserType.CHROMIUM => 60000,
            IWaFixture.BrowserType.FIREFOX => 90000,
            IWaFixture.BrowserType.WEBKIT => 60000,
            _ => throw new ArgumentOutOfRangeException(nameof(_fixture.Type), nameof(_fixture.Type))
        };

        // Increase timeout for large dataset tests (10k records)
        if (name.Contains("LargeDataset", StringComparison.OrdinalIgnoreCase))
        {
            timeout *= 3; // 180-270 seconds for large dataset operations
        }

        if (!_fixture.OnePass)
        {
            await _fixture.Page.GotoAsync($"http://localhost:{_fixture.Port}/Tests/{name}");
        }

        var options = new LocatorAssertionsToBeVisibleOptions()
        {
            Timeout = timeout
        };

        // Accept both OK and SKIPPED as passing results.
        // Use a single locator with an OR clause so that ToBeVisibleAsync
        // throws if NEITHER appears within the timeout. The earlier
        // Task.WhenAny pattern silently swallowed failures: when both
        // Expect(...) tasks faulted, WhenAny returned the first faulted task
        // without us observing its exception, and xUnit counted the test as
        // passed in ~500 ms even though the test page never reached OK.
        var resultLocator = _fixture.Page
            .Locator($"text=SqliteWasm -> {name}: OK")
            .Or(_fixture.Page.Locator($"text=SqliteWasm -> {name}: SKIPPED"));

        await Assertions.Expect(resultLocator).ToBeVisibleAsync(options);
    }
}
