// SqliteWasmBlazor.Components
// MIT License

using SqliteWasmBlazor.Hosting;

namespace SqliteWasmBlazor.Components;

/// <summary>
/// Configuration for the SqliteWasmBlazor.Components package.
/// Passed to <see cref="Interop.FileOperationsInterop.InitializeAsync"/>.
/// </summary>
public sealed class SqliteWasmComponentsOptions : SqliteWasmAssetOptions
{
    public SqliteWasmComponentsOptions()
    {
        AssetRoot = "_content/SqliteWasmBlazor.Components/";
    }
}
