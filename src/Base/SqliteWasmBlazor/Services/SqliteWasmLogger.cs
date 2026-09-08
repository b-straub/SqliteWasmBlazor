// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Runtime.InteropServices.JavaScript;
using Microsoft.Extensions.Logging;

namespace SqliteWasmBlazor;

/// <summary>
/// Configures logging for SQLite WASM worker.
/// Uses Microsoft.Extensions.Logging.LogLevel for consistency with .NET logging infrastructure.
/// </summary>
public static partial class SqliteWasmLogger
{
    /// <summary>
    /// Sets the log level for SQLite WASM worker operations.
    /// Maps Microsoft.Extensions.Logging.LogLevel to TypeScript logger levels:
    /// - None: No logging
    /// - Critical/Error: Only errors
    /// - Warning: Errors and warnings
    /// - Information: Errors, warnings, and info
    /// - Debug/Trace: All messages including debug
    /// </summary>
    /// <param name="level">The desired log level from Microsoft.Extensions.Logging</param>
    public static void SetLogLevel(LogLevel level)
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("SqliteWasmLogger only works in browser context");
        }

        // The managed level takes effect immediately and needs no JS, so this
        // may be called before the worker exists — which is the only way to
        // trace initialization itself.
        _level = level;

        // The two JS halves cannot be configured until the bridge module is
        // loaded and the worker is up; the bridge calls PublishLevel once it
        // is, which is where a level set before that point takes effect.
        if (_workerReady)
        {
            PublishToJs();
        }
    }

    /// <summary>
    /// Whether SQL text and parameter values may be written to the console at
    /// all, on either side of the worker boundary. Off unless the host sets
    /// <c>SqliteWasmOptions.EnableCommandSqlLogging</c>.
    /// </summary>
    /// <remarks>
    /// Orthogonal to the log level: the level decides how verbose logging is,
    /// this decides whether query <i>content</i> — table and column names,
    /// parameter values — is ever emitted. Raising the level to Debug for
    /// timings therefore does not expose data.
    /// </remarks>
    internal static bool CommandSqlLoggingEnabled { get; set; }

    /// <summary>
    /// Whether the request round trip is traced: the bridge's per-request
    /// send / complete / abandon lines with the worker's backlog, and the
    /// worker's own per-statement timing.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of both the log level and
    /// <see cref="CommandSqlLoggingEnabled"/>. Tracing is for benchmarking, so
    /// it has to be usable in a Release build without turning on every other
    /// debug message, and it must never emit SQL text or parameter values —
    /// only ids, counts and durations. Off unless the host sets
    /// <c>SqliteWasmOptions.EnableRequestTracing</c>; when off, nothing is
    /// timed and no message is built on either side of the worker boundary.
    /// </remarks>
    internal static bool TracingEnabled { get; set; }

    /// <summary>
    /// Called by the bridge once the worker is ready, to hand the JS halves the
    /// configuration <see cref="SetLogLevel"/> may have recorded before they
    /// existed. Idempotent.
    /// </summary>
    internal static void PublishLevel()
    {
        _workerReady = true;
        PublishToJs();
    }

    private static void PublishToJs() =>
        ConfigureLoggingInternal(ToWorkerLevel(_level), CommandSqlLoggingEnabled, TracingEnabled);

    /// <summary>Maps to the TypeScript logger's own 0-4 scale.</summary>
    private static int ToWorkerLevel(LogLevel level) => level switch
    {
        LogLevel.None => 0,           // NONE
        LogLevel.Critical => 1,       // ERROR
        LogLevel.Error => 1,          // ERROR
        LogLevel.Warning => 2,        // WARNING
        LogLevel.Information => 3,    // INFO
        LogLevel.Debug => 4,          // DEBUG
        LogLevel.Trace => 4,          // DEBUG
        _ => 2                        // Default to WARNING
    };

    private static bool _workerReady;

    /// <summary>
    /// Mirrors the level handed to <see cref="SetLogLevel"/> so the C# half of
    /// the bridge can skip the JS boundary entirely when debug output is off.
    /// Starts at <see cref="LogLevel.Warning"/>, matching the worker's default.
    /// </summary>
    private static LogLevel _level = LogLevel.Warning;

    /// <summary>
    /// Gets a value indicating whether request tracing is on, as set by
    /// <c>SqliteWasmOptions.EnableRequestTracing</c>.
    /// </summary>
    /// <remarks>
    /// Test this before building a message that costs anything to construct.
    /// <see cref="Trace"/> checks it too, but only after its arguments have
    /// already been evaluated — an interpolated string is built whether or not
    /// the line is ultimately written.
    /// </remarks>
    public static bool IsTracingEnabled => TracingEnabled;

    /// <summary>
    /// Writes one trace line to the browser console, prefixed with
    /// <paramref name="module"/>, so application lines sit in the same stream as
    /// the bridge's and the worker's and read in the order they happened.
    /// </summary>
    /// <param name="module">
    /// Short tag identifying the source, shown as a prefix — e.g. the name of
    /// the page or model doing the work.
    /// </param>
    /// <param name="message">The line to write.</param>
    /// <remarks>
    /// No-ops unless <see cref="IsTracingEnabled"/>.
    /// </remarks>
    public static void Trace(string module, string message)
    {
        if (!TracingEnabled)
        {
            return;
        }

        Console.WriteLine($"[{module}] {message}");
    }

    [JSImport("globalThis.__sqliteWasmLogger.configureLogging")]
    private static partial void ConfigureLoggingInternal(int level, bool commandSql, bool tracing);
}
