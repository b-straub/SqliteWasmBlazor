using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SqliteWasmBlazor.TestApp;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Crypto.Extensions;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;
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
#if DEBUG
    var connection = new SqliteWasmConnection("Data Source=TestDb.db", LogLevel.Debug);
#else
    var connection = new SqliteWasmConnection("Data Source=TestDb.db");
#endif
    
    options.UseSqliteWasm(connection);

    // Only enable detailed logging in Debug builds
#if DEBUG
    options.EnableSensitiveDataLogging();
    options.LogTo(message => Console.WriteLine(message));
#endif
});

// Add CryptoSync test context (separate DB from TodoDb). Routed through the
// PRF-keyed VFS so the integration tests exercise the full production stack:
// shadow + envelope crypto on top of page-level AEAD, not in isolation.
builder.Services.AddDbContextFactory<CryptoTestContext>(options =>
{
    options.UseSqliteWasm("Data Source=CryptoTestDb.db", CryptoTestContext.EncryptionKey);
});

// Add PRF-VFS integration-test context. Opens via the encrypted VFS path
// using the deterministic test key in VfsEncryptionTestBase.TestKey.
builder.Services.AddDbContextFactory<EncryptedTestContext>(options =>
{
    options.UseSqliteWasm(
        $"Data Source={VfsEncryptionTestBase.EncryptedDatabaseName}",
        VfsEncryptionTestBase.TestKey);
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
// SecureKeyCache.UseKey) and installs it directly into the worker
// registry through ISqliteWasmDatabaseService.InstallEncryptionKeyAsync
// before resolving this factory. xOpen picks up the registered key and
// routes through the encrypted VFS — no key envelope flows through C#.
builder.Services.AddDbContextFactory<PrfVfsTestContext>(options =>
{
    options.UseSqliteWasm($"Data Source={PrfVfsTestContext.DatabaseName}");
});

// Register SqliteWasm database management service
builder.Services.AddSqliteWasm(o => o.HostEnvironment = builder.HostEnvironment);

// Register SqliteWasmBlazor.Crypto services (Noble.js + SubtleCrypto)
builder.Services.AddSqliteWasmBlazorCrypto(configure: o => o.HostEnvironment = builder.HostEnvironment);

var host = builder.Build();

// Initialize sqlite-wasm worker
await host.Services.InitializeSqliteWasmAsync();

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
