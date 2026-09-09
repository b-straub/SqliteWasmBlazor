using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SqliteWasmBlazor.TestApp;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Crypto.Extensions;
using SqliteWasmBlazor.TestApp.TestInfrastructure;
using SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;
using SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

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

// Migration-upgrade probe. Its own database and its own pair of migrations,
// because TodoDbContext ships exactly one and an upgrade needs something to
// upgrade from. See MigrationProbeContext.
builder.Services.AddDbContextFactory<MigrationProbeContext>(options =>
{
    options.UseSqliteWasm(new SqliteWasmConnection($"Data Source={MigrationProbeContext.DatabaseName}"));
});

// Add PRF-VFS integration-test context. Opens via the encrypted VFS path
// using the deterministic test key in VfsEncryptionTestBase.TestKey, which
// the fixture installs as the worker-wide global key at setup.
builder.Services.AddDbContextFactory<EncryptedTestContext>(options =>
{
    options.UseSqliteWasm(
        $"Data Source={VfsEncryptionTestBase.EncryptedDatabaseName}");
});

// Plain twin of EncryptedTestContext — same VfsTestItem schema, no key.
// Lets the perf tests compare plain vs encrypted on identical workloads so
// the measured delta is AEAD cost, not schema-complexity cost.
builder.Services.AddDbContextFactory<PlainVfsTestContext>(options =>
{
    options.UseSqliteWasm($"Data Source={PlainVfsTestContext.DatabaseName}");
});

// PRF-VFS demo page context. Registered without a key in DI: the page
// derives the key via SqliteWasmBlazor.Crypto DomainKeys (DeriveDomainKeyAsync +
// SecureKeyCache.UseKey) and installs it as the worker-wide global key via
// ISqliteWasmDatabaseService.SetEncryptionKeyAsync before resolving this
// factory. xOpen picks up globalKey and
// routes through the encrypted VFS — no key envelope flows through C#.
builder.Services.AddDbContextFactory<PrfVfsTestContext>(options =>
{
    options.UseSqliteWasm($"Data Source={PrfVfsTestContext.DatabaseName}");
});

// Register SqliteWasm database management service
var baseHref = new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath;
builder.Services.AddSqliteWasm(o => o.BaseHref = baseHref);

// Declared, not initialized — <SqliteWasmDatabaseInitializer/> and the test
// harness drive this after the first render. TodoDbContext is first on purpose:
// initialization stops at the first context that fails, and the recovery tests
// stage a broken TodoDb schema and assert on the diagnosis it produces.
builder.Services.AddSqliteWasmDbContext<TodoDbContext>();
builder.Services.AddSqliteWasmDbContext<MigrationProbeContext>();

// Which worker bundle this run boots. `?plane=plain` leaves the Crypto
// services unregistered, so the bridge stays on _content/SqliteWasmBlazor/ —
// the only way base's own worker cases (replaceDb, the import sessions, the
// streaming export/import handlers, the init park sweep) are ever executed.
// The plain-safe tests then run against them unchanged.
await TestPlane.ResolveAsync(baseHref);

// Register SqliteWasmBlazor.Crypto services (SubtleCrypto + @awasm/noble)
if (!TestPlane.IsPlain)
{
    builder.Services.AddSqliteWasmBlazorCrypto(configure: o => o.BaseHref = baseHref);
}

// Counting host seam — ImportReconcilesHostSchemaTest asserts that the
// import paths reconcile the host's schema themselves. Declares no owned
// databases, so nothing else in the harness is affected. Registered as a
// singleton rather than through AddHostDatabaseService so the instance the
// bridge resolves and the one the test reads its counter from are the same
// one; a real host has no reason to care.
builder.Services.AddSingleton<TestHostDatabaseService>();
builder.Services.AddSingleton<IHostDatabaseService>(
    sp => sp.GetRequiredService<TestHostDatabaseService>());

// Short TTL keeps the PRF session-expiry E2E test fast. Post-auth ops in
// the other Facts complete inside 1-2s, so 5s leaves comfortable margin.
if (!TestPlane.IsPlain)
{
    builder.Services.Configure<SqliteWasmBlazor.Crypto.Configuration.KeyCacheOptions>(o =>
    {
        o.TtlMs = 5000;
    });
}

var host = builder.Build();

// Worker/bridge log level. Set before initialization so worker startup and the
// database opens are covered; it used to ride on the SqliteWasmConnection
// constructor, which set it process-wide on every context creation.
#if DEBUG
SqliteWasmLogger.SetLogLevel(LogLevel.Debug);
#endif

// Initialization happens after the first render — see
// <SqliteWasmDatabaseInitializer/> in MainLayout, and the harness in
// SqliteWasmTests.razor, which awaits it before staging anything. The clean
// plain-disk recreate that used to sit here moved there too: it needs an
// initialized worker, and the harness already wipes the pool.
await host.RunAsync();
