using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SqliteWasmBlazor.Crypto.Extensions;
using SqliteWasmBlazor.Crypto.UI;
using SqliteWasmBlazor.Crypto.UI.Services;
using SqliteWasmBlazor.Demo.Services;
using SqliteWasmBlazor.FloatingWindow.Extensions;
using SqliteWasmBlazor;
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
    // No log level here: that overload sets the process-wide level, and this
    // lambda runs on every context creation, so it would override the level
    // configured below on every query. See SqliteWasmLogger.SetLogLevel.
    var connection = new SqliteWasmConnection("Data Source=TodoDb.db");
    options.UseSqliteWasm(connection);
});

// Add NoteDbContext with SqliteWasm provider (database: NotesDb.db)
builder.Services.AddDbContextFactory<NoteDbContext>(options =>
{
    // No log level here: that overload sets the process-wide level, and this
    // lambda runs on every context creation, so it would override the level
    // configured below on every query. See SqliteWasmLogger.SetLogLevel.
    var connection = new SqliteWasmConnection("Data Source=NotesDb.db");
    options.UseSqliteWasm(connection);
});

// Register SqliteWasm database management service (also registers IDbInitializationStatus / Reporter)
var baseHref = new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath;
builder.Services.AddSqliteWasm(o => o.BaseHref = baseHref);

// Declare the contexts <SqliteWasmDatabaseInitializer/> migrates, in the order
// it should migrate them. Nothing touches the database here — that happens once
// MainLayout has rendered, so a long migration has somewhere to report.
builder.Services.AddSqliteWasmDbContext<TodoDbContext>();
builder.Services.AddSqliteWasmDbContext<NoteDbContext>();

// Base-plane crypto (SubtleCrypto + @awasm/noble) and the production
// IPrfAuthenticator bridge consumed by Crypto.UI's AuthenticationPanel.
// Salt defaults to PrfOptions.Salt (no user identity in the demo);
// change it if shipping under a different RP.
builder.Services.AddSqliteWasmBlazorCrypto(                                                                                                                                                           
    configurePrf: _ => { },                       // PrfOptions defaults are fine                                                                                                                     
    configureCache: c => c.TtlMs = 300_000,         // 5-minute session for testing                                                                                                                   
    configure:      o => o.BaseHref = baseHref);  
builder.Services.AddCryptoUIPrfAuthenticator();

// Crypto.UI panel models + the singleton StatusModel that every command in
// the library routes errors and status messages to (rendered by
// <StatusDisplay/> in MainLayout). Includes the EncryptionModel — the demo
// composes its own /database-encryption page over that model surface; the
// library doesn't ship a routable page (consumer-side composition is the
// boundary, see Demo/Pages/DatabaseEncryption.razor).
builder.Services.AddCryptoUI();

// Demo-side ObservableModels (TodoListModel, DatabaseEncryptionModel).
// The RxBlazorV2 SG emits ObservableModels.Initialize per assembly; never
// AddScoped<TModel>() directly (per feedback_rxblazor_observable_models_registration).
SqliteWasmBlazor.Demo.ObservableModels.Initialize(builder.Services);

// Localized resx for the Crypto.UI panels (en + de today). Combined with
// <BlazorWebAssemblyLoadAllGlobalizationData>true</> in the csproj, this
// makes navigator.language drive panel text at boot.
builder.Services.AddLocalization();

// Boot progress as snackbar text. The library reports states; this turns them
// into sentences, because the base package has no localization of its own.
builder.Services.AddDbInitNotifier<DemoDbInitNotifier>();

// The host seam, bound to both interfaces it satisfies: the panels resolve
// IHostRecoveryService for the reset affordance (hidden when
// IsAvailable=false, as NullHostRecoveryService reports), the base plane's
// import paths resolve IHostDatabaseService for owned-database names and
// the schema gate. The impl below deletes both Demo DBs and reports
// DbInitState.READY so the alert auto-clears once boot is healthy again.
builder.Services.AddHostRecoveryService<DemoHostDatabaseService>();

// Required by SessionExpiredPopoverModel (registered Scoped by AddCryptoUI
// regardless of whether the popover is mounted in MainLayout) — without
// this binding the DI validator fails the whole graph at boot with
// "CannotResolveService ISessionAuthenticator".
builder.Services.AddScoped<ISessionAuthenticator, DemoSessionAuthenticator>();

var host = builder.Build();

// Eagerly construct EncryptedPoolLifecycle so its AuthenticationStateChanged +
// KeyExpired subscriptions are live before the first AuthorizeView renders.
host.Services.UseEncryptedPoolLifecycle();

// Worker/bridge log level. Set here rather than on the SqliteWasmConnection
// constructor, which applied it process-wide on every context creation. Runs
// before initialization so worker startup and the database opens are covered;
// the bridge forwards it to the JS halves once the worker is up.
//
// Request tracing is deliberately not tied to this — see enableRequestTracing.
#if DEBUG
SqliteWasmLogger.SetLogLevel(LogLevel.Information);
#else
SqliteWasmLogger.SetLogLevel(LogLevel.Error);
#endif

await host.RunAsync();