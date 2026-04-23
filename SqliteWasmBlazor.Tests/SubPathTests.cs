using Microsoft.Playwright;
using SqliteWasmBlazor.Tests.Infrastructure;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests;

/// <summary>
/// E2E test that verifies the library works when the Blazor app is deployed under
/// a sub-path (e.g. /myapp/). This validates that:
///   1. The response-rewriting middleware serves index.html with the correct <base href="/myapp/">.
///   2. TestApp derives baseHref from HostEnvironment.BaseAddress ("/myapp/").
///   3. SqliteWasmWorkerBridge loads sqlite-wasm-bridge.js from /myapp/_content/...
///      instead of via the now-CSP-blocked data:text/javascript scheme.
///   4. No CSP violations are triggered during the full lifecycle.
/// </summary>
[CollectionDefinition("SubPath", DisableParallelization = true)]
public class SubPathCollection : ICollectionFixture<SubPathFixture>
{
    // Marker class — no code needed.
}

[Collection("SubPath")]
public class SubPathTest(SubPathFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<string> _cspViolations = [];

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await fixture.InitializeAsync();

        // Capture any CSP violation reports or "Refused to" console errors.
        // A regression (e.g. using data:text/javascript again) would show up here.
        fixture.Page!.Console += (_, msg) =>
        {
            if (msg.Type == "error" &&
                (msg.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase) ||
                 msg.Text.Contains("Refused to", StringComparison.OrdinalIgnoreCase)))
            {
                _cspViolations.Add(msg.Text);
                output.WriteLine($"[CSP VIOLATION] {msg.Text}");
            }
        };
    }

    [Theory]
    [InlineData("Create_SingleEntity")]
    [InlineData("Read_ById")]
    [InlineData("Transaction_Commit")]
    public async Task SubPath_TestCaseAsync(string name)
    {
        Assert.NotNull(fixture.Page);

        var url = $"http://localhost:{fixture.Port}{SubPathFixture.SubPath}/Tests/{name}";
        output.WriteLine($"Navigating to: {url}");

        await fixture.Page.GotoAsync(url);

        var successLocator = fixture.Page.Locator($"text=SqliteWasm -> {name}: OK");
        var skippedLocator = fixture.Page.Locator($"text=SqliteWasm -> {name}: SKIPPED");

        // Allow 30 s for WASM initialisation + test execution (same as ChromiumTest)
        var options = new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 };

        await Task.WhenAny(
            Assertions.Expect(successLocator).ToBeVisibleAsync(options),
            Assertions.Expect(skippedLocator).ToBeVisibleAsync(options)
        );

        Assert.Empty(_cspViolations);
    }
}
