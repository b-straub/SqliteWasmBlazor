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

    /// <summary>
    /// Optional sub-minute TTL override in milliseconds. When set, takes precedence
    /// over <see cref="TtlMinutes"/>. Used by integration tests to drive the
    /// session-expiry timer path within E2E budgets.
    /// </summary>
    public int? TtlMs { get; set; }
}

/// <summary>
/// Key caching strategy.
/// </summary>
public enum KeyCacheStrategy
{
    /// <summary>
    /// One-shot C# seed cache — <see cref="SqliteWasmBlazor.Crypto.Services.SecureKeyCache"/>
    /// consumes seed / domain-key entries on first <c>TryGet</c> /
    /// <c>UseKey</c>, so HKDF-derived domain keys cannot be regenerated
    /// without a fresh authentication ceremony.
    /// <para>
    /// <b>Scope.</b> Practical only for non-CryptoSync, non-encrypted-VFS
    /// hosts (e.g. plain-DB apps that occasionally derive a domain key
    /// from a PRF seed and never need another cached crypto op). In
    /// CryptoSync / encrypted-VFS configurations every interactive
    /// operation (signing relay POSTs, ECIES-unwrap on import, sealing
    /// invitations) needs the cached key material to remain reachable
    /// — under NONE those flows would require a fresh WebAuthn ceremony
    /// per op, which no UI in this repo drives.
    /// </para>
    /// <para>
    /// The JS-side cache (non-extractable SubtleCrypto handles + X25519
    /// priv buffer addressed by keyId) is session-lifetime under every
    /// strategy; <c>ClearKeys</c> / <c>RemoveCachedKey</c> drops it. The
    /// auth ceremony itself zeroes the seed buffer once both caches have
    /// copied it (P21).
    /// </para>
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
