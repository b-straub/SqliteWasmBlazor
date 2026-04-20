using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.FloatingWindow.Services;

namespace SqliteWasmBlazor.FloatingWindow.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the FloatingWindow services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assetRoot">Static-asset path segment, e.g. "_content/SqliteWasmBlazor.FloatingWindow/". Override for browser-extension builds.</param>
    public static IServiceCollection AddFloatingWindow(
        this IServiceCollection services,
        string assetRoot = "_content/SqliteWasmBlazor.FloatingWindow/")
    {
        services.AddScoped<IWindowManager, WindowManager>();
        services.AddSingleton(new FloatingWindowOptions { AssetRoot = assetRoot });
        return services;
    }
}
