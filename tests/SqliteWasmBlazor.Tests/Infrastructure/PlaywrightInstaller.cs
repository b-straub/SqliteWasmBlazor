namespace SqliteWasmBlazor.Tests.Infrastructure;

internal static class PlaywrightInstaller
{
    private static bool _installed;
    private static readonly object InstallLock = new();

    public static void EnsureInstalled()
    {
        if (_installed)
        {
            return;
        }

        lock (InstallLock)
        {
            if (_installed)
            {
                return;
            }

            var browserCache = Path.Combine(Path.GetTempPath(), "sqlitewasmblazor-playwright-browsers");
            Directory.CreateDirectory(browserCache);
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browserCache);

            if (OperatingSystem.IsLinux())
            {
                var depsExit = Microsoft.Playwright.Program.Main(["install-deps", "chromium"]);
                if (depsExit != 0)
                {
                    throw new InvalidOperationException(
                        $"Playwright exited with code {depsExit} on install-deps chromium");
                }
            }

            var installExit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (installExit != 0)
            {
                throw new InvalidOperationException(
                    $"Playwright exited with code {installExit} on install chromium. " +
                    $"PLAYWRIGHT_BROWSERS_PATH={browserCache}");
            }

            _installed = true;
        }
    }
}
