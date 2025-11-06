using Microsoft.Playwright;
using SqliteWasm.Data.Tests.Infrastructure;

namespace SqliteWasm.Data.Tests;

public abstract class SqliteWasmTestBase(IWaFixture fixture) : IAsyncLifetime
{
    private readonly IWaFixture _fixture = fixture;

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
    // Race Condition Tests
    [InlineData("RaceCondition_PurgeThenLoad")]
    [InlineData("RaceCondition_PurgeThenLoadWithTransaction")]
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

            await _fixture.Page.GotoAsync($"https://localhost:{_fixture.Port}/Tests/{name}");
        }

        var options = new LocatorAssertionsToBeVisibleOptions()
        {
            Timeout = timeout
        };

        await Assertions.Expect(_fixture.Page.Locator($"text=SqliteWasm -> {name}: OK")).ToBeVisibleAsync(options);
    }
}
