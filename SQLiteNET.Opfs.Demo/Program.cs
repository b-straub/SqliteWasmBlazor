using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasm.Demo;
using SqliteWasm.Demo.Data;
using System.Data.SQLite.Wasm;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Reduce EF Core logging verbosity
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Add MudBlazor services
builder.Services.AddMudServices();

// Add DbContext with our new System.Data.SQLite.Wasm provider
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
SqliteWasmLogger.SetLogLevel(SqliteWasmLogLevel.Warning);

// Initialize database - check if tables exist before creating schema
using (var scope = host.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();

    // EnsureCreatedIfNeededAsync will:
    // 1. Check if tables exist by querying sqlite_master
    // 2. Only call EnsureCreatedAsync if no tables found
    // 3. Handle the case where database doesn't exist yet
    var wasCreated = await dbContext.Database.EnsureCreatedIfNeededAsync();
    Console.WriteLine(wasCreated
        ? "[Startup] Database schema created"
        : "[Startup] Database schema already exists, skipping creation");
}

await host.RunAsync();
