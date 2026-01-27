using Microsoft.Extensions.DependencyInjection;

namespace SqliteWasmBlazor.WindowHelper;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFloatingDialogService(this IServiceCollection services)
    {
        services.AddScoped<FloatingDialogService>();
        return services;
    }
}
