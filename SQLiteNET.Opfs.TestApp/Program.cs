using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SQLiteNET.Opfs.TestApp;
using SqliteWasm.Data.Models;
using System.Data.SQLite.Wasm;
using Microsoft.EntityFrameworkCore;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Add DbContext with SqliteWasm provider
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=TestDb.db");
    options.UseSqliteWasm(connection);
    options.EnableSensitiveDataLogging();
    options.LogTo(message => Console.WriteLine(message));
});

var host = builder.Build();

// Initialize sqlite-wasm worker
await SqliteWasmWorkerBridge.Instance.InitializeAsync();

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
