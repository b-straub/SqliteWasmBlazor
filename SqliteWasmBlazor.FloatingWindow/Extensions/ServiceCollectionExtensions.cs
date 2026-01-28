using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.FloatingWindow.Services;

namespace SqliteWasmBlazor.FloatingWindow.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the FloatingWindow services to the service collection.
    /// </summary>
    public static IServiceCollection AddFloatingWindow(this IServiceCollection services)
    {
        services.AddScoped<IWindowManager, WindowManager>();
        return services;
    }
}
