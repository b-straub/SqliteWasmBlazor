using Microsoft.Playwright;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests.Infrastructure;

public class WaFixtureBase : IAsyncDisposable
{
    public IPage? Page { get; private set; }

    private readonly int _port;
    private readonly ITestOutputHelper? _output;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _browserContext;
    private StaticWebAssetTestServer? _server;
    private bool _serverStarted;

    public WaFixtureBase(int port, ITestOutputHelper? output = null)
    {
        _port = port;
        _output = output;
    }

    protected virtual string PathBase => string.Empty;

    protected async Task InitializeAsync(IWaFixture.BrowserType browserType, bool onePass, bool headless)
    {
        PlaywrightInstaller.EnsureInstalled();

        // Start the Kestrel server if not already started
        if (!_serverStarted)
        {
            _server = new StaticWebAssetTestServer(_port, PathBase);
            _server.Start();
            _serverStarted = true;
        }

        if (_playwright is not null)
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();

        var newContextOptions = new BrowserNewContextOptions()
        {
            IgnoreHTTPSErrors = false  // Using HTTP now
        };

        _browser = browserType switch
        {
            IWaFixture.BrowserType.CHROMIUM => await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Args =
                [
                    "--ignore-certificate-errors",
                    "--js-flags=--max-old-space-size=4096",      // 4GB heap for V8 JS engine
                    "--disable-dev-shm-usage",                    // Use /tmp instead of /dev/shm (helps in constrained envs)
                    "--disable-gpu-memory-buffer-video-frames"    // Reduce GPU memory pressure
                ]
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

        // Capture unhandled exceptions in the browser
        _browserContext.WebError += (_, webError) =>
        {
            var message = $"[Browser WebError] {webError.Error}";
            _output?.WriteLine(message);
            Console.Error.WriteLine(message);
        };

        Page = await _browserContext.NewPageAsync();

        // Capture all console messages from the browser
        Page.Console += (_, msg) =>
        {
            var message = $"[Browser {msg.Type}] {msg.Text}";
            _output?.WriteLine(message);

            // Also write to Console so it appears in CI logs even without ITestOutputHelper
            if (msg.Type == "error")
            {
                Console.Error.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        };

        if (onePass)
        {
            int timeout;
            switch (browserType)
            {
                case IWaFixture.BrowserType.CHROMIUM:
                    timeout = 100000;
                    break;
                case IWaFixture.BrowserType.FIREFOX:
                case IWaFixture.BrowserType.WEBKIT:
                    timeout = 300000;
                    break;
                case IWaFixture.BrowserType.NONE:
                case IWaFixture.BrowserType.ALL:
                default:
                    throw new ArgumentOutOfRangeException(nameof(browserType));
            }

            var waitForSelectorOptions = new PageWaitForSelectorOptions()
            {
                Timeout = timeout
            };

            await Page.GotoAsync($"http://localhost:{_port}{PathBase}/Tests");

            await Page.WaitForSelectorAsync("text=All Tests Completed", waitForSelectorOptions);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCoreAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCoreAsync()
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

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
            _serverStarted = false;
        }
    }
}
