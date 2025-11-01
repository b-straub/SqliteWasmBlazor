using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SQLiteNET.Opfs.Abstractions;
using SQLiteNET.Opfs.Services;

namespace SQLiteNET.Opfs.Extensions;

/// <summary>
/// Extension methods for adding OPFS-backed SQLite DbContext
/// </summary>
public static class OpfsDbContextExtensions
{
    /// <summary>
    /// Add OPFS-backed SQLite DbContext with automatic initialization
    /// This replaces the InMemory database with persistent OPFS storage
    /// </summary>
    public static IServiceCollection AddOpfsSqliteDbContext<TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
    {
        // Register OPFS storage service as singleton
        services.AddSingleton<IOpfsStorage, OpfsStorageService>();

        // Register DbContext with SQLite provider
        services.AddDbContext<TContext>((provider, options) =>
        {
            var contextType = typeof(TContext).Name;
            var databaseName = $"{contextType}.db";

            // Use SQLite with OPFS VFS
            // Note: The VFS will be handled by SQLite WASM automatically when in browser
            options.UseSqlite($"Data Source={databaseName}");

            // Apply additional options if provided
            optionsAction?.Invoke(provider, options);
        });

        return services;
    }

    /// <summary>
    /// Configure SQLite journal mode for WASM compatibility
    /// Must be called before Database.MigrateAsync() or Database.EnsureCreatedAsync()
    /// </summary>
    public static async Task ConfigureSqliteForWasmAsync(this DbContext context)
    {
        // Set journal mode to DELETE (WASM doesn't support WAL properly)
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");

        // Additional WASM-friendly settings
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
        await context.Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY;");
    }

    /// <summary>
    /// Initialize OPFS and ensure database is created
    /// </summary>
    public static async Task InitializeOpfsAsync(this IServiceProvider serviceProvider)
    {
        var opfsStorage = serviceProvider.GetRequiredService<IOpfsStorage>();
        var initialized = await opfsStorage.InitializeAsync();

        if (!initialized)
        {
            throw new InvalidOperationException("Failed to initialize OPFS storage");
        }
    }
}
