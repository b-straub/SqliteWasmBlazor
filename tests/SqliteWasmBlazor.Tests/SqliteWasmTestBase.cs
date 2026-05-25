using Microsoft.Playwright;
using SqliteWasmBlazor.TestApp.TestInfrastructure;
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
    [MemberData(nameof(TestRegistry.NamesAsTheoryData), MemberType = typeof(TestRegistry))]
    public async Task TestCaseAsync(string name)
    {
        Assert.NotNull(_fixture.Page);

        // Cover both modes:
        //   OnePass — one shared page load runs every test sequentially. Each
        //     xUnit test polls for its own per-test label, so the wait must
        //     cover the *cumulative* queue, not just one test's runtime.
        //   Per-test — fresh navigation per case; wait covers a single WASM
        //     boot + run.
        // GitHub Actions runners are noticeably slower than a dev box, and
        // OnePass-mode tail-end tests (e.g. TimeSpan_Conversion) wait for the
        // full queue to drain before their label appears. The Chromium budget
        // was 10 s which passed locally but flaked one test on CI with no
        // diagnostic (VSTestTask returned false without logging the actual
        // failure). Bumped to 60 s to match the comment intent and absorb CI
        // jitter; Firefox/WebKit already at the longer values.
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
            var pathBase = _fixture is SubPathFixture ? SubPathFixture.SubPath : string.Empty;
            await _fixture.Page.GotoAsync($"http://localhost:{_fixture.Port}{pathBase}/Tests/{name}");
        }

        var options = new LocatorAssertionsToBeVisibleOptions()
        {
            Timeout = timeout
        };

        var resultLocator = _fixture.Page.Locator($"text=SqliteWasm -> {name}:");
        await Assertions.Expect(resultLocator).ToBeVisibleAsync(options);

        var resultText = await resultLocator.InnerTextAsync();
        Output.WriteLine(resultText);
        Assert.True(
            resultText.EndsWith(": OK", StringComparison.Ordinal) ||
            resultText.EndsWith(": SKIPPED", StringComparison.Ordinal),
            resultText);
    }
}
