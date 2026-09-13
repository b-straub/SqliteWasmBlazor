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

    /// <summary>
    /// Assert one test case's result label. Not a <c>[Theory]</c> itself —
    /// each derived class declares its own over the name list its fixture's
    /// plane actually runs.
    /// </summary>
    protected async Task RunCaseAsync(string name)
    {
        Assert.NotNull(_fixture.Page);

        // Per-test mode navigates fresh, so this wait covers one WASM boot
        // plus the case, sized for a GitHub Actions runner. In one-pass mode
        // the fixture has already waited for the run to end — the queue
        // budget lives there, scaled by the queue length — so the label is
        // either on the page or not coming, and this wait only absorbs a
        // render.
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
            await _fixture.Page.GotoAsync(
                $"http://localhost:{_fixture.Port}/Tests/{name}{_fixture.Query}");
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

        // A harness failure — a TestFactory/TestRegistry drift, a DI resolve
        // that threw — renders as the runner's error banner and no labels at
        // all. Waiting only on the label turns that into every case burning
        // its full timeout, which reads as a hang rather than the one-line
        // misconfiguration it is. Waiting on either lets the banner fail the
        // first case immediately, with its text.
        var errorLocator = _fixture.Page.Locator("#test-harness-error");

        await Assertions.Expect(resultLocator.Or(errorLocator)).ToBeVisibleAsync(options);

        if (await errorLocator.IsVisibleAsync())
        {
            Assert.Fail($"Test harness failed to run: {await errorLocator.InnerTextAsync()}");
        }
    }
}
