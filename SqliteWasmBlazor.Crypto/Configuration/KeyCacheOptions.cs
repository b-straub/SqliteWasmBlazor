namespace SqliteWasmBlazor.Crypto.Configuration;

/// <summary>
/// Configuration options for key caching.
/// </summary>
public sealed class KeyCacheOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "SqliteWasmBlazorCrypto:KeyCache";

    /// <summary>
    /// Key caching strategy.
    /// </summary>
    public KeyCacheStrategy Strategy { get; set; } = KeyCacheStrategy.TIMED;

    /// <summary>
    /// Time-to-live in minutes for cached keys (only used with Timed strategy).
    /// </summary>
    public int TtlMinutes { get; set; } = 15;
}

/// <summary>
/// Key caching strategy.
/// </summary>
public enum KeyCacheStrategy
{
    /// <summary>
    /// No caching - keys are derived fresh for each operation.
    /// Most secure but requires user interaction each time.
    /// </summary>
    NONE,

    /// <summary>
    /// Session caching - keys are cached until page refresh.
    /// Balance between security and usability.
    /// </summary>
    SESSION,

    /// <summary>
    /// Timed caching - keys expire after TTL.
    /// Recommended for most applications.
    /// </summary>
    TIMED
}
