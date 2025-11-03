namespace SQLiteNET.Opfs.Abstractions;

/// <summary>
/// Log levels for OPFS operations (similar to MSBuild verbosity).
/// </summary>
public enum OpfsLogLevel
{
    /// <summary>
    /// No logging output.
    /// </summary>
    None = 0,

    /// <summary>
    /// Only log errors that prevent operation.
    /// </summary>
    Error = 1,

    /// <summary>
    /// Log errors and warnings (default).
    /// </summary>
    Warning = 2,

    /// <summary>
    /// Log errors, warnings, and informational messages.
    /// </summary>
    Info = 3,

    /// <summary>
    /// Log everything including debug information.
    /// </summary>
    Debug = 4
}
