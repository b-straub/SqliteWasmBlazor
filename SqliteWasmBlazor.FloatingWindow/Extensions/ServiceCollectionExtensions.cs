using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.FloatingWindow.Services;

namespace SqliteWasmBlazor.FloatingWindow.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the FloatingWindow services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback. Override <see cref="FloatingWindowOptions.AssetRoot"/>
    /// for browser-extension builds.</param>
    public static IServiceCollection AddFloatingWindow(
        this IServiceCollection services,
        Action<FloatingWindowOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<FloatingWindowOptions>();
        }

        services.AddScoped<IWindowManager, WindowManager>();
        return services;
    }
}
