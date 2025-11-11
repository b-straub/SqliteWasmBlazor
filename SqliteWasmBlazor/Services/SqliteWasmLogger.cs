// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Runtime.InteropServices.JavaScript;

namespace SqliteWasmBlazor;

/// <summary>
/// Log level for SQLite WASM operations.
/// Matches TypeScript enum SqliteWasmLogLevel.
/// </summary>
public enum SqliteWasmLogLevel
{
    /// <summary>No logging</summary>
    NONE = 0,
    /// <summary>Only errors</summary>
    ERROR = 1,
    /// <summary>Errors and warnings</summary>
    WARNING = 2,
    /// <summary>Errors, warnings, and info</summary>
    INFO = 3,
    /// <summary>All messages including debug</summary>
    DEBUG = 4
}

/// <summary>
/// Configures logging for SQLite WASM worker.
/// </summary>
public static partial class SqliteWasmLogger
{
    /// <summary>
    /// Sets the log level for SQLite WASM worker operations.
    /// </summary>
    /// <param name="level">The desired log level</param>
    public static void SetLogLevel(SqliteWasmLogLevel level)
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("SqliteWasmLogger only works in browser context");
        }

        SetLogLevelInternal((int)level);
    }

    [JSImport("globalThis.__sqliteWasmLogger.setLogLevel")]
    private static partial void SetLogLevelInternal(int level);
}
