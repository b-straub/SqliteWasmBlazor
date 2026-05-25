using Microsoft.Playwright;
using SqliteWasmBlazor.Tests.Infrastructure;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests;

public sealed class ChromiumTransactionCompatibilityTests(
    ChromiumPerTestFixture fixture,
    ITestOutputHelper output)
    : IClassFixture<ChromiumPerTestFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DisposeRollsBackAndDoesNotLeaveWorkerTransactionOpen()
    {
        await RunTestAsync("Transaction_DisposeRollsBack");
        output.WriteLine("Transaction_DisposeRollsBack passed in Chromium.");
    }

    [Fact]
    public async Task ConcurrentBeginWaitsForActiveWorkerTransaction()
    {
        await RunTestAsync("Transaction_ConcurrentBeginSerializes");
        output.WriteLine("Transaction_ConcurrentBeginSerializes passed in Chromium.");
    }

    [Fact]
    public async Task IndependentCommandWaitsForActiveWorkerTransaction()
    {
        await RunTestAsync("Transaction_BlocksIndependentCommand");
        output.WriteLine("Transaction_BlocksIndependentCommand passed in Chromium.");
    }

    [Fact]
    public async Task SavepointRollbackAndReleaseMatchSQLite()
    {
        await RunTestAsync("Transaction_Savepoint");
        output.WriteLine("Transaction_Savepoint passed in Chromium.");
    }

    private async Task RunTestAsync(string testName)
    {
        Assert.NotNull(fixture.Page);

        await fixture.Page.GotoAsync($"http://localhost:{fixture.Port}/Tests/{testName}");

        var resultLocator = fixture.Page.Locator($"text=SqliteWasm -> {testName}:");
        await Assertions.Expect(resultLocator).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 60000
        });

        var resultText = await resultLocator.InnerTextAsync();
        output.WriteLine(resultText);
        Assert.EndsWith(": OK", resultText, StringComparison.Ordinal);
    }
}
