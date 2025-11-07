using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SQLiteNET.Opfs.TestApp;
using SqliteWasm.Data.Models;
using System.Data.SQLite.Wasm;
using Microsoft.EntityFrameworkCore;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Reduce EF Core logging verbosity in production
#if !DEBUG
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
// Suppress warning about SqliteWasmConnection type not being recognized by EF Core
// Warning: "A connection of an unexpected type (SqliteWasmConnection) is being used.
//           The SQL functions prefixed with 'ef_' could not be created automatically."
// This is INFORMATIONAL ONLY - ef_ functions ARE registered manually in the worker (see ef-core-functions.ts)
// All decimal arithmetic, aggregate, comparison, and regex functions work correctly via worker registration
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Infrastructure", LogLevel.Error);
#endif

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Add DbContext with SqliteWasm provider
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=TestDb.db");
    options.UseSqliteWasm(connection);

    // Only enable detailed logging in Debug builds
#if DEBUG
    options.EnableSensitiveDataLogging();
    options.LogTo(message => Console.WriteLine(message));
#endif
});

var host = builder.Build();

// Initialize sqlite-wasm worker
await SqliteWasmWorkerBridge.Instance.InitializeAsync();

// Set worker log level to Debug in debug builds for detailed function tracing
#if DEBUG
SqliteWasmLogger.SetLogLevel(SqliteWasmLogLevel.DEBUG);
#endif

// Initialize database - always recreate for clean test runs
using (var scope = host.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();

    // Use EF Core migrations with custom SqliteWasmHistoryRepository
    // The custom history repository disables the infinite polling lock mechanism
    await dbContext.Database.EnsureDeletedAsync();
    await dbContext.Database.MigrateAsync();

    Console.WriteLine("[TestApp] Database deleted and migrated");
}

await host.RunAsync();
