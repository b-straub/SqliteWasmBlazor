using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests.Infrastructure;

public class WaFixtureBase : WebApplicationFactory<TestHost.Program>
{
    public IPage? Page { get; private set; }

    private readonly int _port;
    private readonly ITestOutputHelper? _output;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _browserContext;
    private bool _serverStarted;

    public WaFixtureBase(int port, ITestOutputHelper? output = null)
    {
        _port = port;
        _output = output;

        // Use the new .NET 10 API - must be called in constructor before server initialization
        UseKestrel(port);
    }

    // queueLength: how many cases a one-pass run executes before it reports
    // completion. Sizes the wait for that report; unused per-test.
    protected async Task InitializeAsync(
        IWaFixture.BrowserType browserType, bool onePass, bool headless, string query = "",
        int queueLength = 0)
    {
        PlaywrightInstaller.EnsureInstalled();

        // Start the Kestrel server if not already started
        if (!_serverStarted)
        {
            StartServer();
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
            if (queueLength <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(queueLength), queueLength, "A one-pass run needs its queue length to size the wait.");
            }

            // The budget for the whole queue is per case, so a new case brings
            // its own share instead of eating into a fixed total. The allowance
            // is what a case may cost on a GitHub Actions runner, where the
            // Crypto plane averages ~2 s per case and the plain plane a little
            // less; a dev box is several times faster. The 20k-row migration
            // cases are the outliers that set the margin — each takes ~20 s
            // there.
            int perCaseMs;
            switch (browserType)
            {
                case IWaFixture.BrowserType.CHROMIUM:
                    perCaseMs = 5000;
                    break;
                case IWaFixture.BrowserType.FIREFOX:
                case IWaFixture.BrowserType.WEBKIT:
                    perCaseMs = 15000;
                    break;
                case IWaFixture.BrowserType.NONE:
                case IWaFixture.BrowserType.ALL:
                default:
                    throw new ArgumentOutOfRangeException(nameof(browserType));
            }

            var options = new LocatorWaitForOptions()
            {
                Timeout = perCaseMs * queueLength
            };

            await Page.GotoAsync($"http://localhost:{_port}/Tests{query}");

            // The runner ends either with its completion line or with the
            // harness error banner. Waiting on both lets a TestFactory or
            // DI failure surface at once — through RunCaseAsync, which reads
            // the banner — instead of after the full queue budget.
            var completed = Page.Locator("text=All Tests Completed");
            var harnessError = Page.Locator("#test-harness-error");

            await completed.Or(harnessError).WaitForAsync(options);
        }
    }

    public override async ValueTask DisposeAsync()
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
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use HTTP for testing to avoid SSL certificate issues
        builder.UseUrls($"http://localhost:{_port}");

        // Suppress verbose logging during tests
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }

}
