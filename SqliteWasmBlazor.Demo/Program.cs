using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SqliteWasmBlazor.Demo.Services;
using SqliteWasmBlazor.WindowHelper;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Demo;
using SqliteWasmBlazor.Models;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Reduce EF Core logging verbosity
#if DEBUG
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information);
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

// Add floating dialog service (draggable/resizable dialogs)
builder.Services.AddFloatingDialogService();

// Add data change notification service for multi-view synchronization
builder.Services.AddSingleton<TodoDataNotifier>();

// Add DbContext with SqliteWasm provider
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
#if DEBUG
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db", LogLevel.Information);
#else
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db", LogLevel.Error);
#endif
    options.UseSqliteWasm(connection);
});

// Register database initialization service
builder.Services.AddSingleton<IDBInitializationService, DBInitializationService>();

// Initialize FileOperations JS module for import/export
await FileOperationsInterop.InitializeAsync();

var host = builder.Build();

// Initialize SqliteWasm database with migration support
// Log level is configured via SqliteWasmConnection constructor above
await host.Services.InitializeSqliteWasmDatabaseAsync<TodoDbContext>();

await host.RunAsync();