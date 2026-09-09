using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SqliteWasmBlazor;
using SqliteWasmBlazor.AdoNetSample;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddSqliteWasm(o => o.BaseHref = new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath);

var host = builder.Build();

// No initialization here. <SqliteWasmDatabaseInitializer/> in MainLayout starts
// the worker once the app has rendered; with no DbContext declared there is
// nothing to migrate.
await host.RunAsync();
