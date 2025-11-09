using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Demo;
using SqliteWasmBlazor.Demo.Services;
using SqliteWasmBlazor.Models;
using SqliteWasmLogger = SqliteWasmBlazor.SqliteWasmLogger;
using SqliteWasmWorkerBridge = SqliteWasmBlazor.SqliteWasmWorkerBridge;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Reduce EF Core logging verbosity
#if DEBUG
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
#else
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Infrastructure", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
#endif

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Add MudBlazor services
builder.Services.AddMudServices();

// Initialize sqlite-wasm worker
string? errorMessage = null;

try
{
    await SqliteWasmWorkerBridge.Instance.InitializeAsync();
}
catch (Exception ex)
{
    errorMessage =
$"""
{ex.Message}
Database is locked by another browser tab.
This application uses OPFS (Origin Private File System) which only allows one tab to access the database at a time.
Please close any other tabs running this application and refresh the page.
""";
}

if (errorMessage is null)
{
    // Add DbContext with our new SqliteWasmBlazor provider
    builder.Services.AddDbContextFactory<TodoDbContext>(options =>
    {
        // Use our worker-based SqliteWasmConnection with OPFS storage
        var connection = new SqliteWasmConnection("Data Source=TodoDb.db");
        options.UseSqliteWasm(connection); // Uses custom database creator that handles OPFS
    });
}

builder.Services.AddSingleton(_ => new DBInitializationService(errorMessage));

var host = builder.Build();

if (errorMessage is null)
{
    // Initialize database - create if it doesn't exist
    using var scope = host.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();

    var initService = scope.ServiceProvider.GetRequiredService<DBInitializationService>();
    try
    {
        // Apply pending migrations only (skip if database is already up to date)
        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
        if (pendingMigrations.Any())
        {
            await dbContext.Database.MigrateAsync();
        }
        
        // Configure logging - set to Warning to reduce chatty debug logs
        // Use SqliteWasmLogLevel.Debug for detailed SQL execution logs
        SqliteWasmLogger.SetLogLevel(SqliteWasmLogLevel.WARNING);
    }
    catch (TimeoutException)
    {
        initService.ErrorMessage = 
"""
Database is locked by another browser tab.
This application uses OPFS (Origin Private File System) which only allows one tab to access the database at a time.
Please close any other tabs running this application and refresh the page.
""";
    }
    catch (Exception ex)
    {
        initService.ErrorMessage = $"ERROR initializing database: {ex.GetType().Name}: {ex.Message}";
        initService.ErrorMessage += Environment.NewLine;
        initService.ErrorMessage += $"{ex.StackTrace}";
    }
}

await host.RunAsync();