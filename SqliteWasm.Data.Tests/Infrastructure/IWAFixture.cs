using Microsoft.Playwright;

namespace SqliteWasm.Data.Tests.Infrastructure;

public interface IWAFixture
{
    public enum BrowserType
    {
        None = 0,
        Chromium = 1,
        Firefox = 2,
        Webkit = 4,
        All = 7
    }

    public Task InitializeAsync();
    public IPage? Page { get; }
    public BrowserType Type { get; }
    public int Port { get; }
    public bool OnePass { get; }
    public bool Headless { get; }
}
