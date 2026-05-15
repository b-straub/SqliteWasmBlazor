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

        // Map LogLevel to TypeScript logger levels (0-4)
        var workerLevel = level switch
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

        SetLogLevelInternal(workerLevel);
    }

    [JSImport("globalThis.__sqliteWasmLogger.setLogLevel")]
    private static partial void SetLogLevelInternal(int level);
}
