using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasm.Demo;
using SqliteWasm.Demo.Data;
using System.Data.SQLite.Wasm;
using System.Runtime.Versioning;

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
SqliteWasmLogger.SetLogLevel(SqliteWasmLogLevel.WARNING);

// Initialize database - create if it doesn't exist
using (var scope = host.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();

    // Use standard EF Core method - it checks if database exists and creates it if needed
    var wasCreated = await dbContext.Database.EnsureCreatedAsync();
    Console.WriteLine(wasCreated
        ? "[Startup] Database schema created"
        : "[Startup] Database schema already exists, skipping creation");

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

partial class Program { }
