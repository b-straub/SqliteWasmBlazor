namespace SqliteWasm.Data.Tests.Infrastructure;

public class ChromiumFixture : WAFixtureBase, IWAFixture
{
    public IWAFixture.BrowserType Type => IWAFixture.BrowserType.Chromium;
    public int Port => PortNumber;
    public bool OnePass => false;
    public bool Headless => true; // Set to false to see browser during tests

    private static int PortNumber => 7051;

    public ChromiumFixture() : base(PortNumber) { }

    public async Task InitializeAsync()
    {
        await InitializeAsync(Type, OnePass, Headless);
    }
}

public class FirefoxFixture : WAFixtureBase, IWAFixture
{
    public IWAFixture.BrowserType Type => IWAFixture.BrowserType.Firefox;
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

public class WebkitFixture : WAFixtureBase, IWAFixture
{
    public IWAFixture.BrowserType Type => IWAFixture.BrowserType.Webkit;
    public int Port => PortNumber;
    public bool OnePass => false;
    public bool Headless => false;

    private static int PortNumber => 7053;

    public WebkitFixture() : base(PortNumber) { }

    public async Task InitializeAsync()
    {
        await InitializeAsync(Type, OnePass, Headless);
    }
}
