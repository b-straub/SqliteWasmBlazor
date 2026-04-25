// SqliteWasmBlazor.FloatingWindow
// MIT License

namespace SqliteWasmBlazor.FloatingWindow;

/// <summary>
/// Configuration for the FloatingWindow package.
/// Configure via <c>services.AddFloatingWindow(o => ...)</c> — resolved from DI as <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>.
/// </summary>
public sealed class FloatingWindowOptions
{
    /// <summary>
    /// Path segment between the app <c>&lt;base href&gt;</c> and the package file names.
    /// Defaults to "_content/SqliteWasmBlazor.FloatingWindow/". Override to
    /// "content/SqliteWasmBlazor.FloatingWindow/" for Blazor.BrowserExtension builds.
    /// </summary>
    public string AssetRoot { get; set; } = "_content/SqliteWasmBlazor.FloatingWindow/";
}
