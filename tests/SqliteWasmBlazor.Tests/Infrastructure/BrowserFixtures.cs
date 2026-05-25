namespace SqliteWasmBlazor.Tests.Infrastructure;

/// <summary>
/// Fixture that serves the TestApp under a sub-path (/myapp) to exercise
/// the baseHref parameter introduced by the CSP-hardening change.
/// </summary>
public class SubPathFixture : WaFixtureBase, IWaFixture
{
    /// <summary>The path base used for sub-path deployment testing.</summary>
    public const string SubPath = "/myapp";

    private static int PortNumber => 7054;

    public IWaFixture.BrowserType Type => IWaFixture.BrowserType.CHROMIUM;
    public int Port => PortNumber;
    public bool OnePass => false;
    public bool Headless => true;

    public SubPathFixture() : base(PortNumber) { }

    protected override string PathBase => SubPath;

    public async Task InitializeAsync()
    {
        await InitializeAsync(Type, OnePass, Headless);
    }
}

public class ChromiumFixture : WaFixtureBase, IWaFixture
{
    public IWaFixture.BrowserType Type => IWaFixture.BrowserType.CHROMIUM;
    public int Port => PortNumber;
    // OnePass: navigate to /Tests once, run all tests in a single Blazor
    // instance, and have each xUnit test assert its own per-test result
    // label — instead of paying full WASM boot per test (the dominant cost).
    public bool OnePass => !bool.TryParse(
        Environment.GetEnvironmentVariable("SQLITEWASM_TEST_ONEPASS"),
        out var onePass) || onePass;
    public bool Headless => true;

    private static int PortNumber => 7051;

    public ChromiumFixture() : base(PortNumber) { }

    public async Task InitializeAsync()
    {
        await InitializeAsync(Type, OnePass, Headless);
    }
}

public class ChromiumPerTestFixture : WaFixtureBase, IWaFixture
{
    public IWaFixture.BrowserType Type => IWaFixture.BrowserType.CHROMIUM;
    public int Port => PortNumber;
    public bool OnePass => false;
    public bool Headless => true;

    private static int PortNumber => 7055;

    public ChromiumPerTestFixture() : base(PortNumber) { }

    public async Task InitializeAsync()
    {
        await InitializeAsync(Type, OnePass, Headless);
    }
}

// Firefox and WebKit tests disabled due to Playwright compatibility issues
// Firefox: Working in browser but disabled for now
// WebKit: Out of memory errors in Playwright (works fine in Safari)
#if NEVER_DEFINED
public class FirefoxFixture : WaFixtureBase, IWaFixture
{
    public IWaFixture.BrowserType Type => IWaFixture.BrowserType.FIREFOX;
    public int Port => PortNumber;
    public bool OnePass => false;
    public bool Headless => true;

    private static int PortNumber => 7052;

    public FirefoxFixture() : base(PortNumber) { }

    public async Task InitializeAsync()
    {
        await InitializeAsync(Type, OnePass, Headless);
    }
}

public class WebkitFixture : WaFixtureBase, IWaFixture
{
    public IWaFixture.BrowserType Type => IWaFixture.BrowserType.WEBKIT;
    public int Port => PortNumber;
    public bool OnePass => false;
    public bool Headless => true;

    private static int PortNumber => 7053;

    public WebkitFixture() : base(PortNumber) { }

    public async Task InitializeAsync()
    {
        await InitializeAsync(Type, OnePass, Headless);
    }
}
#endif
