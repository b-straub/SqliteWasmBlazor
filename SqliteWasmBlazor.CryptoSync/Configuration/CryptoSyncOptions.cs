namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Host-bound options for the CryptoSync transport stack registered via
/// <see cref="CryptoSyncServiceCollectionExtensions.AddCryptoSync{TContext}(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration?, System.Action{CryptoSyncOptions}?)"/>.
/// Bind from <c>appsettings.json</c> under <see cref="SectionName"/> or set
/// programmatically via the <c>configure</c> callback. The transport
/// (<see cref="HttpSyncTransport"/>) and admin-side push client
/// (<see cref="WhitelistPushService"/>) both target <see cref="RelayBaseUri"/>.
/// </summary>
public sealed class CryptoSyncOptions
{
    /// <summary>Configuration section name. Use as
    /// <c>configuration.GetSection(CryptoSyncOptions.SectionName)</c>.</summary>
    public const string SectionName = "CryptoSync";

    /// <summary>
    /// Absolute URL of the delta relay (whitelist-broadcast PHP endpoint).
    /// Resolves <c>/api/delta</c> + <c>/api/whitelist</c> against this base.
    /// Required at resolve time — the registered transport throws if unset.
    /// </summary>
    public string? RelayBaseUri { get; set; }
}
