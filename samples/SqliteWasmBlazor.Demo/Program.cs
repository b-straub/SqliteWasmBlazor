using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SqliteWasmBlazor.Crypto.Extensions;
using SqliteWasmBlazor.Crypto.UI;
using SqliteWasmBlazor.Crypto.UI.Services;
using SqliteWasmBlazor.Demo.Services;
using SqliteWasmBlazor.FloatingWindow.Extensions;
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

// Add FloatingWindow service
builder.Services.AddFloatingWindow();

// Add data change notification service for multi-view synchronization
builder.Services.AddSingleton<TodoDataNotifier>();

// Add TodoDbContext with SqliteWasm provider (database: TodoDb.db)
builder.Services.AddDbContextFactory<TodoDbContext>(options =>
{
#if DEBUG
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db", LogLevel.Information);
#else
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db", LogLevel.Error);
#endif
    options.UseSqliteWasm(connection);
});

// Add NoteDbContext with SqliteWasm provider (database: NotesDb.db)
builder.Services.AddDbContextFactory<NoteDbContext>(options =>
{
#if DEBUG
    var connection = new SqliteWasmConnection("Data Source=NotesDb.db", LogLevel.Information);
#else
    var connection = new SqliteWasmConnection("Data Source=NotesDb.db", LogLevel.Error);
#endif
    options.UseSqliteWasm(connection);
});

// Register SqliteWasm database management service (also registers IDbInitializationStatus / Reporter)
var baseHref = new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath;
builder.Services.AddSqliteWasm(o => o.BaseHref = baseHref);

// Base-plane crypto (Noble.js + SubtleCrypto) and the production
// IPrfAuthenticator bridge consumed by Crypto.UI's RegistrationPanel +
// AuthenticationPanel. Salt defaults to PrfOptions.Salt (no user identity
// in the demo); change it if shipping under a different RP.
builder.Services.AddSqliteWasmBlazorCrypto(configure: o => o.BaseHref = baseHref);
builder.Services.AddCryptoUIPrfAuthenticator();

// Crypto.UI panel models + the singleton StatusModel that every command in
// the library routes errors and status messages to (rendered by
// <StatusDisplay/> in MainLayout).
builder.Services.AddCryptoUI();

// Localized resx for the Crypto.UI panels (en + de today). Combined with
// <BlazorWebAssemblyLoadAllGlobalizationData>true</> in the csproj, this
// makes navigator.language drive panel text at boot.
builder.Services.AddLocalization();

// Demo-side recovery callback for <DatabaseErrorAlert/>: the panel hides
// the reset button when IsAvailable=false (NullDatabaseResetService); the
// real impl below deletes both Demo DBs and reports DbInitState.READY so
// the alert auto-clears once the boot status is healthy again.
builder.Services.AddScoped<IDatabaseResetService, DemoDatabaseResetService>();

// Initialize FileOperations JS module for import/export
await FileOperationsInterop.InitializeAsync();

var host = builder.Build();

// Initialize SqliteWasm databases with migration support
await host.Services.InitializeSqliteWasmDatabaseAsync<TodoDbContext>();
await host.Services.InitializeSqliteWasmDatabaseAsync<NoteDbContext>();

await host.RunAsync();