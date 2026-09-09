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

    /// <summary>
    /// How long a migration has to be running before
    /// <see cref="DbInitState.MIGRATING"/> is reported. Defaults to 500 ms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whether a migration is worth showing is a question about duration, and
    /// nothing available beforehand answers it. "Are migrations pending?" says
    /// yes on every first run, because a database with none applied has all of
    /// them pending — and that work is a <c>CREATE TABLE</c> on an empty file.
    /// So the announcement waits to see: work that finishes inside this window
    /// is never announced, and work that does not gets a progress state for as
    /// long as it runs.
    /// </para>
    /// <para>
    /// Set to <see cref="TimeSpan.Zero"/> to announce every migration.
    /// </para>
    /// </remarks>
    public TimeSpan MigrationAnnounceDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
