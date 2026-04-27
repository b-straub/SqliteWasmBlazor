// SqliteWasmBlazor.FloatingWindow
// MIT License

using SqliteWasmBlazor.Hosting;

namespace SqliteWasmBlazor.FloatingWindow;

/// <summary>
/// Configuration for the FloatingWindow package.
/// Configure via <c>services.AddFloatingWindow(o => ...)</c> — resolved from DI as <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>.
/// </summary>
public sealed class FloatingWindowOptions : SqliteWasmAssetOptions
{
    public FloatingWindowOptions()
    {
        AssetRoot = "_content/SqliteWasmBlazor.FloatingWindow/";
    }
}
