// SqliteWasmBlazor.Components
// MIT License

namespace SqliteWasmBlazor.Components;

/// <summary>
/// Configuration for the SqliteWasmBlazor.Components package.
/// Passed to <see cref="Interop.FileOperationsInterop.InitializeAsync"/>.
/// </summary>
public sealed class SqliteWasmComponentsOptions
{
    /// <summary>
    /// Path segment between the app <c>&lt;base href&gt;</c> and the package file names.
    /// Defaults to "_content/SqliteWasmBlazor.Components/". Override to
    /// "content/SqliteWasmBlazor.Components/" for Blazor.BrowserExtension builds.
    /// </summary>
    public string AssetRoot { get; set; } = "_content/SqliteWasmBlazor.Components/";
}
