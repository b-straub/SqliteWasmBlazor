using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SQLiteNET.Opfs.Demo;
using SQLiteNET.Opfs.Demo.Data;
using SQLiteNET.Opfs.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Reduce EF Core logging verbosity
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Add MudBlazor services
builder.Services.AddMudServices();

// Add OPFS-backed SQLite DbContext with EF Core
builder.Services.AddOpfsDbContextFactory<TodoDbContext>(options =>
{
    options.UseSqlite("Data Source=TodoDb.db");
});

var host = builder.Build();

// Initialize OPFS storage
await host.Services.InitializeOpfsAsync();

// Initialize database
using (var scope = host.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();

    // Configure SQLite for WASM (journal mode)
    await dbContext.Database.ConfigureSqliteForWasmAsync();

    // Ensure database schema is created
    await dbContext.Database.EnsureCreatedAsync();
}

await host.RunAsync();
