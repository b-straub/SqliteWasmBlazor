namespace SqliteWasmBlazor.Tests.Infrastructure;

internal static class PlaywrightInstaller
{
    private static bool _installed;

    public static void EnsureInstalled()
    {
        if (_installed)
        {
            return;
        }

        var depsExit = Microsoft.Playwright.Program.Main(["install-deps"]);
        if (depsExit != 0)
        {
            throw new InvalidOperationException(
                $"Playwright exited with code {depsExit} on install-deps");
        }

        var installExit = Microsoft.Playwright.Program.Main(["install"]);
        if (installExit != 0)
        {
            throw new InvalidOperationException(
                $"Playwright exited with code {installExit} on install");
        }

        _installed = true;
    }
}
