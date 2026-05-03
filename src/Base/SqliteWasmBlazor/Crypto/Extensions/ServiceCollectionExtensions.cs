using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

// ReSharper disable once CheckNamespace
namespace SqliteWasmBlazor.Crypto.Extensions;

/// <summary>
/// Extension methods for registering SqliteWasmBlazorCrypto services with Noble.js + SubtleCrypto provider.
/// </summary>
[SupportedOSPlatform("browser")]
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add SqliteWasmBlazorCrypto services with Noble.js crypto provider to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Optional configuration for binding <see cref="PrfOptions"/> and
    /// <see cref="KeyCacheOptions"/>. <see cref="SqliteWasmBlazorCryptoOptions"/> (asset resolution) is configured
    /// via the <paramref name="configure"/> callback because it requires the runtime
    /// <c>IWebAssemblyHostEnvironment.BaseAddress</c> for sub-path deployments (passed
    /// from the consuming app, kept out of this library to avoid the WebAssembly dep).</param>
    /// <param name="configure">Optional callback to configure asset resolution. For
    /// sub-path deployments set <see cref="Hosting.SqliteWasmAssetOptions.BaseHref"/>
    /// (e.g. <c>new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath</c>); for
    /// browser-extension builds override <see cref="Hosting.SqliteWasmAssetOptions.AssetRoot"/>.</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddSqliteWasmBlazorCrypto(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<SqliteWasmBlazorCryptoOptions>? configure = null)
    {
        // PRF + cache options (bind from configuration when supplied)
        if (configuration is not null)
        {
            services.Configure<PrfOptions>(configuration.GetSection(PrfOptions.SectionName));
            services.Configure<KeyCacheOptions>(configuration.GetSection(KeyCacheOptions.SectionName));
        }
        else
        {
            services.Configure<PrfOptions>(_ => { });
            services.Configure<KeyCacheOptions>(_ => { });
        }

        // Asset resolution — runtime-only (no appsettings binding because the
        // BaseHref is derived from IWebAssemblyHostEnvironment.BaseAddress in
        // the consuming app, not expressible in JSON).
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<SqliteWasmBlazorCryptoOptions>();
        }

        // Register crypto provider
        services.AddSingleton<ICryptoProvider, NobleCryptoProvider>();

        // Register services
        services.AddSingleton<ISecureKeyCache, SecureKeyCache>();
        services.AddSingleton<PrfService>();
        services.AddSingleton<IPrfService>(sp => sp.GetRequiredService<PrfService>());
        services.AddSingleton<IEd25519PublicKeyProvider>(sp => sp.GetRequiredService<PrfService>());
        services.AddSingleton<ISymmetricEncryption, SymmetricEncryptionService>();
        services.AddSingleton<IAsymmetricEncryption, AsymmetricEncryptionService>();
        services.AddSingleton<ISigningService, SigningService>();

        // Default passkey-hint provider: browser localStorage keyed by salt.
        // Hosts can replace with a synced backend (CryptoSync table, etc.)
        // via the standard DI replacement pattern.
        services.AddSingleton<IPasskeyHintProvider, LocalStoragePasskeyHintProvider>();

        return services;
    }

    /// <summary>
    /// Add SqliteWasmBlazorCrypto services with custom configuration callbacks.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configurePrf">Action to configure PRF options</param>
    /// <param name="configureCache">Optional action to configure cache options</param>
    /// <param name="configure">Optional callback to configure asset resolution. For
    /// sub-path deployments set <see cref="Hosting.SqliteWasmAssetOptions.BaseHref"/>
    /// (e.g. <c>new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath</c>).</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddSqliteWasmBlazorCrypto(
        this IServiceCollection services,
        Action<PrfOptions> configurePrf,
        Action<KeyCacheOptions>? configureCache = null,
        Action<SqliteWasmBlazorCryptoOptions>? configure = null)
    {
        services.Configure(configurePrf);
        services.Configure(configureCache ?? (_ => { }));

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<SqliteWasmBlazorCryptoOptions>();
        }

        // Register crypto provider
        services.AddSingleton<ICryptoProvider, NobleCryptoProvider>();

        // Register services — Singleton across the board: SecureKeyCache holds
        // the canonical key bundle for the app's lifetime, and downstream
        // consumers (IPrfAuthenticator, AuthenticationModel, signing /
        // encryption services) are Singleton too. A Scoped cache would
        // contradict the caching contract — re-deriving keys per scope
        // defeats the TtlMinutes / TtlMs window. Mirrors the lifetimes of
        // the IConfiguration overload above.
        services.AddSingleton<ISecureKeyCache, SecureKeyCache>();
        services.AddSingleton<PrfService>();
        services.AddSingleton<IPrfService>(sp => sp.GetRequiredService<PrfService>());
        services.AddSingleton<IEd25519PublicKeyProvider>(sp => sp.GetRequiredService<PrfService>());
        services.AddSingleton<ISymmetricEncryption, SymmetricEncryptionService>();
        services.AddSingleton<IAsymmetricEncryption, AsymmetricEncryptionService>();
        services.AddSingleton<ISigningService, SigningService>();

        // Default passkey-hint provider: browser localStorage keyed by salt.
        services.AddSingleton<IPasskeyHintProvider, LocalStoragePasskeyHintProvider>();

        return services;
    }
}
