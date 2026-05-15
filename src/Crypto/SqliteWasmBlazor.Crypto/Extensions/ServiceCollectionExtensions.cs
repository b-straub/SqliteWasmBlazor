using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

// ReSharper disable once CheckNamespace
namespace SqliteWasmBlazor.Crypto.Extensions;

/// <summary>
/// Extension methods for registering SqliteWasmBlazorCrypto services with SubtleCrypto + @awasm/noble provider.
/// </summary>
[SupportedOSPlatform("browser")]
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add SqliteWasmBlazorCrypto services with the SubtleCrypto-based provider to the service collection.
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

        // Override plane-1's worker bundle URL to point at plane-2's bundle
        // (`_content/SqliteWasmBlazor.Crypto/sqlite-wasm-worker.js`), which
        // includes the vfs-prf VFS + crypto handlers needed for encrypted
        // operations. PostConfigure runs after the consumer's AddSqliteWasm()
        // Configure callback so this override wins for plane-2 consumers.
        // Browser-extension consumers needing a different path can apply a
        // later PostConfigure of their own.
        services.PostConfigure<SqliteWasmOptions>(
            opt => opt.AssetRoot = "_content/SqliteWasmBlazor.Crypto/");

        // Register crypto provider
        services.AddSingleton<ICryptoProvider, SubtleCryptoProvider>();

        // Register services
        services.AddSingleton<ISecureKeyCache, SecureKeyCache>();
        services.AddSingleton<PrfService>();
        services.AddSingleton<IPrfService>(sp => sp.GetRequiredService<PrfService>());

        // Encrypted-disk lifecycle. Lives here (not in AddSqliteWasm) because
        // it depends on IPrfService for the implicit ClearKeys cascade in
        // ResetDiskAsync — once the disk is wiped, the PRF session for the
        // just-erased identity is moot. Plain-only consumers that don't call
        // this extension never see the encrypted-disk surface.
        services.AddSingleton<EncryptedSqliteWasmDatabaseService>();
        services.AddSingleton<IEncryptedSqliteWasmDatabaseService>(sp => sp.GetRequiredService<EncryptedSqliteWasmDatabaseService>());
        // Plane-1-facing probe so InitializeSqliteWasmDatabaseAsync<TContext>
        // can detect ENCRYPTED_LOCKED boot state without seeing plane-2 types.
        services.AddSingleton<IDatabaseLockProbe>(sp => sp.GetRequiredService<EncryptedSqliteWasmDatabaseService>());

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

        // Override plane-1's worker bundle URL to point at plane-2's bundle
        // (`_content/SqliteWasmBlazor.Crypto/sqlite-wasm-worker.js`), which
        // includes the vfs-prf VFS + crypto handlers needed for encrypted
        // operations. PostConfigure runs after the consumer's AddSqliteWasm()
        // Configure callback so this override wins for plane-2 consumers.
        // Browser-extension consumers needing a different path can apply a
        // later PostConfigure of their own.
        services.PostConfigure<SqliteWasmOptions>(
            opt => opt.AssetRoot = "_content/SqliteWasmBlazor.Crypto/");

        // Register crypto provider
        services.AddSingleton<ICryptoProvider, SubtleCryptoProvider>();

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

        // Encrypted-disk lifecycle — see the IConfiguration overload above
        // for the rationale (depends on IPrfService for ResetDisk cascade).
        services.AddSingleton<EncryptedSqliteWasmDatabaseService>();
        services.AddSingleton<IEncryptedSqliteWasmDatabaseService>(sp => sp.GetRequiredService<EncryptedSqliteWasmDatabaseService>());
        // Plane-1-facing probe so InitializeSqliteWasmDatabaseAsync<TContext>
        // can detect ENCRYPTED_LOCKED boot state without seeing plane-2 types.
        services.AddSingleton<IDatabaseLockProbe>(sp => sp.GetRequiredService<EncryptedSqliteWasmDatabaseService>());

        return services;
    }
}
