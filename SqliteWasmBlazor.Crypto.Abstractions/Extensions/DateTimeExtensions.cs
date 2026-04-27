namespace SqliteWasmBlazor.Crypto.Abstractions.Extensions;

/// <summary>
/// DateTime extension methods for Unix timestamp conversion.
/// Uses DateTime instead of DateTimeOffset.ToUnixTimeSeconds() to avoid
/// IL Trimmer issues in Blazor WASM Release builds.
/// </summary>
public static class DateTimeExtensions
{
    /// <summary>
    /// Gets the current UTC time as Unix timestamp (seconds since 1970-01-01).
    /// </summary>
    public static long ToUnixSeconds(this DateTime dateTime)
    {
        return (long)dateTime.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
    }

    /// <summary>
    /// Converts a Unix timestamp to DateTime (UTC).
    /// </summary>
    public static DateTime FromUnixSeconds(this long unixSeconds)
    {
        return DateTime.UnixEpoch.AddSeconds(unixSeconds);
    }

    /// <summary>
    /// Gets the current UTC time as Unix timestamp.
    /// </summary>
    public static long GetUnixSecondsNow()
    {
        return DateTime.UtcNow.ToUnixSeconds();
    }
}
