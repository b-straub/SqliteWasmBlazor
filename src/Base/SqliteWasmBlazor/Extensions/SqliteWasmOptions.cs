// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using SqliteWasmBlazor.Hosting;

namespace SqliteWasmBlazor;

/// <summary>
/// Configuration for SqliteWasmBlazor worker and asset resolution.
/// Registered via <see cref="SqliteWasmServiceCollectionExtensions.AddSqliteWasm(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{SqliteWasmOptions}?)"/>.
/// </summary>
public sealed class SqliteWasmOptions : SqliteWasmAssetOptions
{
    /// <summary>
    /// Creates the options with <c>AssetRoot</c> pointing at this package's
    /// static web assets. Override it only for a host that serves them from
    /// somewhere else, such as a browser-extension build.
    /// </summary>
    public SqliteWasmOptions()
    {
        AssetRoot = "_content/SqliteWasmBlazor/";
    }

    /// <summary>
    /// Enables logging of executed SQL commands and parameters to the browser console.
    /// This is disabled by default to prevent leaking sensitive application schema or data.
    /// </summary>
    public bool EnableCommandSqlLogging { get; set; }

    /// <summary>
    /// Traces the request round trip for benchmarking: the bridge logs each
    /// request's id, the worker's outstanding backlog, how long it took and
    /// whether the caller abandoned it, and the worker logs each statement's
    /// execution time. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Independent of the log level and of
    /// <see cref="EnableCommandSqlLogging"/>, so it can be turned on in a
    /// Release build without every other debug message coming with it. The
    /// output carries ids, counts and durations only — never SQL text or
    /// parameter values. When off, nothing is timed and no message is built.
    /// </remarks>
    public bool EnableRequestTracing { get; set; }
}
