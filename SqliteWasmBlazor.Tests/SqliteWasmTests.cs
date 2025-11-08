using SqliteWasmBlazor.Tests.Infrastructure;

namespace SqliteWasmBlazor.Tests;

[CollectionDefinition("Chromium", DisableParallelization = true)]
public class ChromiumCollection : ICollectionFixture<ChromiumFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

[Collection("Chromium")]
public class ChromiumTest(ChromiumFixture fixture) : SqliteWasmTestBase(fixture)
{
}

// Firefox and WebKit tests disabled due to Playwright compatibility issues
// Firefox: Working in browser but disabled for now
// WebKit: Out of memory errors in Playwright (works fine in Safari)
#if NEVER_DEFINED
[CollectionDefinition("Firefox", DisableParallelization = true)]
public class FirefoxCollection : ICollectionFixture<FirefoxFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

[Collection("Firefox")]
public class FirefoxTest(FirefoxFixture fixture) : SqliteWasmTestBase(fixture)
{
}

#if !Windows
[CollectionDefinition("Webkit", DisableParallelization = true)]
public class WebkitCollection : ICollectionFixture<WebkitFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

[Collection("Webkit")]
public class WebkitTest(WebkitFixture fixture) : SqliteWasmTestBase(fixture)
{
}
#endif
#endif
