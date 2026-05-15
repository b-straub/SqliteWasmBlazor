using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests.Infrastructure;

/// <summary>
/// Class fixture for the PRF / virtual-authenticator E2E suite. Boots a
/// Kestrel host serving the TestApp WASM and a single shared Chromium
/// instance. Each test calls <see cref="CreateScenarioAsync"/> to obtain
/// an isolated browser context + virtual authenticator.
/// </summary>
public sealed class PrfWaFixture : WebApplicationFactory<TestHost.Program>, IAsyncLifetime
{
    public const int Port = 7060;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _serverStarted;

    public PrfWaFixture()
    {
        UseKestrel(Port);
    }

    public IBrowser Browser =>
        _browser ?? throw new InvalidOperationException("Browser not initialized — InitializeAsync must run first.");

    public async Task InitializeAsync()
    {
        PlaywrightInstaller.EnsureInstalled();

        if (!_serverStarted)
        {
            StartServer();
            _serverStarted = true;
        }

        _playwright ??= await Playwright.CreateAsync();
        _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args =
            [
                "--ignore-certificate-errors",
                "--js-flags=--max-old-space-size=4096",
                "--disable-dev-shm-usage",
                "--disable-gpu-memory-buffer-video-frames",
            ],
        });
    }

    public Task<PrfScenario> CreateScenarioAsync(ITestOutputHelper? output = null)
        => PrfScenario.CreateAsync(Browser, Port, output);

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }

    public override async ValueTask DisposeAsync()
    {
        await ((IAsyncLifetime)this).DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseUrls($"http://localhost:{Port}");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }
}
