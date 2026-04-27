namespace SqliteWasmBlazor.Hosting;

/// <summary>
/// Shared base for the SqliteWasmBlazor package option types that resolve JS / WASM
/// assets at runtime. Each consumer derives a sealed concrete type and sets its own
/// <see cref="AssetRoot"/> default in the constructor.
/// </summary>
/// <remarks>
/// Kept as a plain POCO with two string properties so this base type stays free of
/// the <c>Microsoft.AspNetCore.Components.WebAssembly</c> dependency. Callers that
/// have an <c>IWebAssemblyHostEnvironment</c> in scope set <see cref="BaseHref"/>
/// inline:
/// <c>o.BaseHref = new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath;</c>
/// </remarks>
public abstract class SqliteWasmAssetOptions
{
    /// <summary>
    /// Base href of the Blazor app — origin-side path prefix. Defaults to "/".
    /// For sub-path deployments set this to the absolute path of
    /// <c>IWebAssemblyHostEnvironment.BaseAddress</c>.
    /// </summary>
    public string BaseHref { get; set; } = "/";

    /// <summary>
    /// Path segment between <see cref="BaseHref"/> and the package's asset file
    /// names. Subclasses set their own default in the constructor (e.g.
    /// <c>"_content/SqliteWasmBlazor.Components/"</c>). Override at registration
    /// time to <c>"content/&lt;Package&gt;/"</c> for Blazor.BrowserExtension
    /// builds, which flatten the underscore-prefixed path.
    /// </summary>
    public string AssetRoot { get; set; } = string.Empty;
}
