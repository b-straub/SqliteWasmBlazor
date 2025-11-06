using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace SqliteWasm.Data.Tests.Infrastructure;

public class WaFixtureBase(int port) : WebApplicationFactory<Program>
{
    public IPage? Page { get; private set; }

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _browserContext;
    private readonly int _port = port;

    protected async Task InitializeAsync(IWaFixture.BrowserType browserType, bool onePass, bool headless)
    {
        CreateDefaultClient();

        if (_playwright is not null)
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();

        var newContextOptions = new BrowserNewContextOptions()
        {
            IgnoreHTTPSErrors = true
        };

        _browser = browserType switch
        {
            IWaFixture.BrowserType.CHROMIUM => await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Args = new[] { "--ignore-certificate-errors" }
            }),
            IWaFixture.BrowserType.FIREFOX => await _playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                FirefoxUserPrefs = new Dictionary<string, object>() { { "security.enterprise_roots.enabled", false } }
            }),
            IWaFixture.BrowserType.WEBKIT => await _playwright.Webkit.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(browserType))
        };

        _browserContext = await _browser.NewContextAsync(newContextOptions);

        Page = await _browserContext.NewPageAsync();

        if (onePass)
        {
            var timeout = browserType switch
            {
                IWaFixture.BrowserType.CHROMIUM => 100000,
                IWaFixture.BrowserType.FIREFOX => 300000,
                IWaFixture.BrowserType.WEBKIT => 300000,
                _ => throw new ArgumentOutOfRangeException(nameof(browserType))
            };

            var waitForSelectorOptions = new PageWaitForSelectorOptions()
            {
                Timeout = timeout
            };

            await Page.GotoAsync($"https://localhost:{_port}/Tests");

            await Page.WaitForSelectorAsync("text=All Tests Completed", waitForSelectorOptions);
        }
    }

    public async override ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_browserContext is not null)
        {
            await _browserContext.DisposeAsync();
            _browserContext = null;
        }

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }

        if (_playwright is not null)
        {
            _playwright.Dispose();
            _playwright = null;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseUrls($"https://localhost:{_port}");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        InstallPlaywright();

        // need to create a plain host that we can return.
        var dummyHost = builder.Build();

        // configure and start the actual host.
        builder.ConfigureWebHost(webHostBuilder => webHostBuilder.UseKestrel());

        var host = builder.Build();
        host.Start();

        return dummyHost;
    }

    private static void InstallPlaywright()
    {
        var exitCode = Microsoft.Playwright.Program.Main(
          new[] { "install-deps" });

        if (exitCode != 0)
        {
            throw new Exception(
              $"Playwright exited with code {exitCode} on install-deps");
        }
        exitCode = Microsoft.Playwright.Program.Main(new[] { "install" });
        if (exitCode != 0)
        {
            throw new Exception(
              $"Playwright exited with code {exitCode} on install");
        }
    }
}
