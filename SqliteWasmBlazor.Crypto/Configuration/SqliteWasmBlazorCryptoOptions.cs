using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SqliteWasmBlazor.Crypto.Extensions;

namespace SqliteWasmBlazor.Crypto.Configuration;

/// <summary>
/// Configuration for SqliteWasmBlazor.Crypto JavaScript asset resolution. Registered via
/// <see cref="ServiceCollectionExtensions.AddSqliteWasmBlazorCrypto"/>.
/// </summary>
public sealed class SqliteWasmBlazorCryptoOptions
{
    /// <summary>
    /// Base href of the Blazor app — origin-side path prefix.
    /// Defaults to "/". For sub-path deployments prefer setting <see cref="HostEnvironment"/>,
    /// which derives <see cref="BaseHref"/> from the runtime <c>&lt;base href&gt;</c>.
    /// </summary>
    public string BaseHref { get; set; } = "/";

    /// <summary>
    /// Path segment between <see cref="BaseHref"/> and package file names.
    /// Defaults to "_content/SqliteWasmBlazor.Crypto/" (standard Blazor static-asset convention).
    /// Override to "content/SqliteWasmBlazor.Crypto/" for Blazor.BrowserExtension builds,
    /// which flatten the underscore-prefixed path.
    /// </summary>
    public string AssetRoot { get; set; } = "_content/SqliteWasmBlazor.Crypto/";

    /// <summary>
    /// Convenience setter: derives <see cref="BaseHref"/> from
    /// <see cref="IWebAssemblyHostEnvironment.BaseAddress"/>, which Blazor already
    /// resolves from the baked-in <c>&lt;base href&gt;</c>.
    /// </summary>
    public IWebAssemblyHostEnvironment HostEnvironment
    {
        set => BaseHref = new Uri(value.BaseAddress).AbsolutePath;
    }
}
