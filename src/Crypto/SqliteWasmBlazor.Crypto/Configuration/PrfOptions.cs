namespace SqliteWasmBlazor.Crypto.Configuration;

/// <summary>
/// Configuration options for PRF-based encryption.
/// </summary>
public sealed class PrfOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "SqliteWasmBlazorCrypto";

    /// <summary>
    /// Display name of the relying party shown during WebAuthn registration.
    /// </summary>
    public string RpName { get; set; } = "SqliteWasmBlazorCrypto App";

    /// <summary>
    /// Relying party ID (domain). If null, uses window.location.hostname.
    /// </summary>
    public string? RpId { get; set; }

    /// <summary>
    /// Timeout in milliseconds for WebAuthn operations.
    /// </summary>
    public int TimeoutMs { get; set; } = 60000;

    /// <summary>
    /// Type of authenticator to use.
    /// Platform = built-in biometrics (Touch ID, Windows Hello)
    /// CrossPlatform = USB/NFC security keys (few support PRF)
    /// </summary>
    public AuthenticatorAttachment AuthenticatorAttachment { get; set; } = AuthenticatorAttachment.PLATFORM;

    /// <summary>
    /// App-wide PRF salt used for every ceremony under this <see cref="Services.PrfService"/>
    /// instance. Domain separation for derived keys belongs in the HKDF <c>context</c>
    /// argument to <see cref="Services.IPrfService.DeriveDomainKeyAsync"/>, not in a per-call
    /// salt — one app has one salt.
    /// </summary>
    public string Salt { get; set; } = "my-encryption-keypair";
}

/// <summary>
/// Authenticator attachment type.
/// </summary>
public enum AuthenticatorAttachment
{
    /// <summary>
    /// Platform authenticator (Touch ID, Windows Hello, Face ID).
    /// This is the recommended default as most hardware keys don't support PRF.
    /// </summary>
    PLATFORM,

    /// <summary>
    /// Cross-platform authenticator (USB/NFC security keys).
    /// Many modern hardware keys (YubiKey 5+, SoloKeys v2) support the PRF extension.
    /// </summary>
    CROSS_PLATFORM,

    /// <summary>
    /// Allow both platform and cross-platform authenticators.
    /// The browser will show all available options to the user.
    /// </summary>
    ANY
}
