namespace SqliteWasmBlazor.Tests.Infrastructure;

public class ChromiumFixture : WaFixtureBase, IWaFixture
{
    public IWaFixture.BrowserType Type => IWaFixture.BrowserType.CHROMIUM;
    public int Port => PortNumber;
    public bool OnePass => false;
    public bool Headless => true;

    private static int PortNumber => 7051;

    public ChromiumFixture() : base(PortNumber) { }

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
