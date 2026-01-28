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
    // Checkpoint Tests
    [InlineData("RestoreToCheckpoint_Basic")]
    [InlineData("RestoreToCheckpoint_WithDeltaReapply")]
    public async Task TestCaseAsync(string name)
    {
        Assert.NotNull(_fixture.Page);

        var timeout = 500;

        if (!_fixture.OnePass)
        {
            timeout = _fixture.Type switch
            {
                IWaFixture.BrowserType.CHROMIUM => 30000,  // 30 seconds for WASM initialization
                IWaFixture.BrowserType.FIREFOX => 50000,
                IWaFixture.BrowserType.WEBKIT => 30000,
                _ => throw new ArgumentOutOfRangeException(nameof(_fixture.Type), nameof(_fixture.Type))
            };

            // Increase timeout for large dataset tests (10k records)
            if (name.Contains("LargeDataset", StringComparison.OrdinalIgnoreCase))
            {
                timeout *= 3; // 90-150 seconds for large dataset operations
            }

            await _fixture.Page.GotoAsync($"http://localhost:{_fixture.Port}/Tests/{name}");
        }

        var options = new LocatorAssertionsToBeVisibleOptions()
        {
            Timeout = timeout
        };

        // Accept both OK and SKIPPED as passing results
        var successLocator = _fixture.Page.Locator($"text=SqliteWasm -> {name}: OK");
        var skippedLocator = _fixture.Page.Locator($"text=SqliteWasm -> {name}: SKIPPED");

        // Wait for either OK or SKIPPED
        await Task.WhenAny(
            Assertions.Expect(successLocator).ToBeVisibleAsync(options),
            Assertions.Expect(skippedLocator).ToBeVisibleAsync(options)
        );
    }
}
