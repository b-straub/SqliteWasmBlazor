namespace SqliteWasmBlazor.FloatingWindow;

/// <summary>
/// Options for the FloatingWindow package. Register via <c>AddFloatingWindow(assetRoot: ...)</c>.
/// </summary>
public sealed class FloatingWindowOptions
{
    /// <summary>
    /// Path prefix for static assets, e.g. "_content/SqliteWasmBlazor.FloatingWindow/".
    /// Override for browser-extension builds where Blazor.BrowserExtension flattens the
    /// path (e.g. "content/SqliteWasmBlazor.FloatingWindow/").
    /// </summary>
    public string AssetRoot { get; init; } = "_content/SqliteWasmBlazor.FloatingWindow/";
}
