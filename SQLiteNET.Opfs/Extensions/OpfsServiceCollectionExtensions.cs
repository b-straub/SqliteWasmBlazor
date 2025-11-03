using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SQLiteNET.Opfs.Abstractions;
using SQLiteNET.Opfs.Factories;
using SQLiteNET.Opfs.Interceptors;
using SQLiteNET.Opfs.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering OPFS-backed EF Core DbContext with dependency injection.
/// </summary>
public static class OpfsServiceCollectionExtensions
{
    /// <summary>
    /// Registers a DbContext with OPFS persistence support.
    /// Provides full EF Core functionality with automatic syncing to Origin Private File System.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type to register</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="optionsAction">Configuration action for DbContext options</param>
    /// <param name="dbContextInitializer">Optional custom initialization logic</param>
    /// <param name="opfsLogLevel">OPFS logging verbosity (default: Warning)</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddOpfsDbContextFactory<TDbContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder>? optionsAction = null,
        Func<IServiceProvider, TDbContext, ValueTask>? dbContextInitializer = null,
        OpfsLogLevel opfsLogLevel = OpfsLogLevel.Warning)
        where TDbContext : DbContext
    {
        // Register core services (singleton - shared across app)
        services.AddSingleton<OpfsStorageService>(sp =>
        {
            var jsRuntime = sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>();
            var service = new OpfsStorageService(jsRuntime)
            {
                LogLevel = opfsLogLevel
            };
            return service;
        });
        services.AddSingleton<IOpfsStorage>(sp => sp.GetRequiredService<OpfsStorageService>());
        services.AddSingleton<OpfsDbContextInterceptor>();

        // Register DbContext factory with OPFS support
        services.AddDbContextFactory<TDbContext, OpfsPooledDbContextFactory<TDbContext>>(
            (serviceProvider, options) =>
            {
                // Add interceptor for automatic persistence
                options.AddInterceptors(serviceProvider.GetRequiredService<OpfsDbContextInterceptor>());

                // Apply user configuration
                optionsAction?.Invoke(serviceProvider, options);
            });

        // Register initializer if provided
        if (dbContextInitializer is not null)
        {
            services.TryAddSingleton(dbContextInitializer);
        }

        return services;
    }

    /// <summary>
    /// Registers a DbContext with OPFS persistence support (simplified overload).
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type to register</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="optionsAction">Configuration action for DbContext options</param>
    /// <param name="opfsLogLevel">OPFS logging verbosity (default: Warning)</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddOpfsDbContextFactory<TDbContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction,
        OpfsLogLevel opfsLogLevel = OpfsLogLevel.Warning)
        where TDbContext : DbContext
    {
        return services.AddOpfsDbContextFactory<TDbContext>(
            (_, options) => optionsAction(options),
            null,
            opfsLogLevel);
    }

    /// <summary>
    /// Initializes OPFS storage. Must be called before using the DbContext.
    /// </summary>
    /// <param name="services">The service provider</param>
    /// <returns>A task representing the async initialization</returns>
    /// <exception cref="InvalidOperationException">If OPFS initialization fails</exception>
    public static async Task InitializeOpfsAsync(this IServiceProvider services)
    {
        var storage = services.GetRequiredService<IOpfsStorage>();

        var success = await storage.InitializeAsync();

        if (!success)
        {
            throw new InvalidOperationException(
                "Failed to initialize OPFS storage. " +
                "Ensure the application is running in a modern browser with OPFS support.");
        }
    }
}
