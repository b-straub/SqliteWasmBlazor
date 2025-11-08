using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Demo;
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

// Add DbContext with our new SqliteWasmBlazor provider
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
    // Use our worker-based SqliteWasmConnection with OPFS storage
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db");
    options.UseSqliteWasm(connection); // Uses custom database creator that handles OPFS
});

var host = builder.Build();

// Initialize sqlite-wasm worker
await SqliteWasmWorkerBridge.Instance.InitializeAsync();

// Configure logging - set to Warning to reduce chatty debug logs
// Use SqliteWasmLogLevel.Debug for detailed SQL execution logs
SqliteWasmLogger.SetLogLevel(SqliteWasmLogLevel.WARNING);

// Initialize database - create if it doesn't exist
using (var scope = host.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();

    // Apply pending migrations only (skip if database is already up to date)
    //await dbContext.Database.EnsureDeletedAsync();
    var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
    if (pendingMigrations.Any())
    {
        await dbContext.Database.MigrateAsync();
    }
    // Diagnostic: Test if we can query the database
    try
    {
        var todoCount = await dbContext.TodoItems.CountAsync();
        Console.WriteLine($"[Startup] Database connection verified - {todoCount} todos found");

        var typeTestCount = await dbContext.TypeTests.CountAsync();
        Console.WriteLine($"[Startup] Database connection verified - {typeTestCount} type tests found");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] ERROR querying database: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"[Startup] Stack: {ex.StackTrace}");
    }
}

await host.RunAsync();

namespace SqliteWasmBlazor.Demo
{
    partial class Program { }
}
