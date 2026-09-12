// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SqliteWasmBlazor;

/// <summary>
/// Extension methods for configuring SqliteWasm services.
/// </summary>
public static class SqliteWasmServiceCollectionExtensions
{
    /// <summary>
    /// Registers SqliteWasm services and configuration. Call this in Program.cs before
    /// <c>WebAssemblyHostBuilder.Build()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback. For sub-path deployments
    /// set <see cref="Hosting.SqliteWasmAssetOptions.BaseHref"/> (e.g.
    /// <c>new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath</c>); for
    /// browser-extension builds override <see cref="Hosting.SqliteWasmAssetOptions.AssetRoot"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqliteWasm(
        this IServiceCollection services,
        Action<SqliteWasmOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<SqliteWasmOptions>();
        }

        services.AddSingleton<ISqliteWasmDatabaseService>(SqliteWasmWorkerBridge.Instance);

        // Encrypted-disk lifecycle (IEncryptedSqliteWasmDatabaseService) is
        // registered by AddSqliteWasmBlazorCrypto — it depends on IPrfService
        // for the implicit PRF cache cleanup on ResetPool. Plain-only
        // consumers (AdoNetSample) only call AddSqliteWasm and never see
        // the encrypted-disk surface.
        services.AddSingleton<DbInitializationService>();
        services.AddSingleton<IDbInitializationStatus>(sp => sp.GetRequiredService<DbInitializationService>());
        services.AddSingleton<IDbInitializationReporter>(sp => sp.GetRequiredService<DbInitializationService>());

        // Resolved through the interfaces above rather than the concrete
        // service: Crypto.UI replaces both with DbStateModel, and initialization
        // must report through whichever is registered.
        //
        // Two seams are resolved lazily, as Func<T?>. The lock probe is
        // implemented by a service that takes this one in its own constructor,
        // so asking for the instance here is a container cycle. The notifier is
        // optional, and a resolver keeps "not registered" a null rather than a
        // construction-time failure.
        services.AddSingleton<ISqliteWasmInitializer>(sp => new SqliteWasmInitializer(
            sp,
            sp.GetRequiredService<IDbInitializationReporter>(),
            sp.GetRequiredService<IDbInitializationStatus>(),
            sp.GetRequiredService<IOptions<SqliteWasmOptions>>(),
            sp.GetServices<DbContextSchemaDescriptor>(),
            sp.GetService<IDatabaseLockProbe>,
            sp.GetService<IDbInitNotifier>));

        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="THost"/> as the host seam the import
    /// paths consult — which databases this app owns, and whether what an
    /// import brought is a valid one of them.
    ///
    /// <para>
    /// One class, one call. Consumers of <c>SqliteWasmBlazor.Crypto.UI</c>
    /// use its <c>AddHostRecoveryService</c> instead, which binds the same
    /// instance to the recovery interface those panels resolve as well.
    /// </para>
    /// <para>
    /// Registered as a singleton, and <typeparamref name="THost"/> must be
    /// able to be one. Its consumers are singletons — the worker bridge and
    /// the initializer — resolving from the root provider, where a scoped
    /// registration throws under scope validation, which Blazor turns on in
    /// Development. A seam consulted by a singleton is a singleton.
    /// </para>
    /// </summary>
    /// <typeparam name="THost">The host's implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHostDatabaseService<THost>(
        this IServiceCollection services)
        where THost : class, IHostDatabaseService
    {
        services.AddSingleton<THost>();
        services.AddSingleton<IHostDatabaseService>(sp => sp.GetRequiredService<THost>());
        return services;
    }

    /// <summary>
    /// Declares a context whose pending migrations
    /// <c>&lt;SqliteWasmDatabaseInitializer/&gt;</c> should apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declaration, not execution — nothing touches the database until the app
    /// has rendered. Call it once per context, next to the matching
    /// <c>AddDbContextFactory</c>.
    /// </para>
    /// <para>
    /// <b>Order matters.</b> Contexts are migrated in the order declared and the
    /// sequence stops at the first failure, so the diagnosis a host most wants
    /// to see should be declared first.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The context to migrate.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqliteWasmDbContext<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddSingleton(DbContextSchemaDescriptor.For<TContext>());
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TNotifier"/> as the sink for
    /// initialization progress, so the app can tell its user what is happening
    /// while a migration runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional. Hosts that register none get
    /// <see cref="NullDbInitNotifier"/> and no notifications.
    /// </para>
    /// <para>
    /// Registered as a singleton, and <typeparamref name="TNotifier"/> must be
    /// able to be one: the initializer is a singleton and resolves this from
    /// the root provider, where a scoped registration throws under scope
    /// validation — which Blazor turns on in Development. A sink consumed by a
    /// singleton is a singleton; a scoped one would be a different instance
    /// every time it was told something.
    /// </para>
    /// </remarks>
    /// <typeparam name="TNotifier">The host's implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDbInitNotifier<TNotifier>(
        this IServiceCollection services)
        where TNotifier : class, IDbInitNotifier
    {
        services.AddSingleton<TNotifier>();
        services.AddSingleton<IDbInitNotifier>(sp => sp.GetRequiredService<TNotifier>());
        return services;
    }
}
