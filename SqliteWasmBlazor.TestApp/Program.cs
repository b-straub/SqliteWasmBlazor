using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SqliteWasmBlazor.TestApp;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using BlazorPRF.Crypto.Extensions;
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

// Add CryptoSync test context (separate DB from TodoDb)
builder.Services.AddDbContextFactory<CryptoTestContext>(options =>
{
#if DEBUG
    var cryptoConnection = new SqliteWasmConnection("Data Source=CryptoTestDb.db", LogLevel.Debug);
#else
    var cryptoConnection = new SqliteWasmConnection("Data Source=CryptoTestDb.db");
#endif
    options.UseSqliteWasm(cryptoConnection);
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

// Register SqliteWasm database management service
builder.Services.AddSqliteWasm();

// Register BlazorPRF crypto services (Noble.js + SubtleCrypto)
builder.Services.AddBlazorPrf();

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
