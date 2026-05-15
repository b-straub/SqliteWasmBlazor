using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SqliteWasmBlazor.TestApp;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Crypto.Extensions;
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

// Register SqliteWasmBlazor.Crypto services (SubtleCrypto + @awasm/noble)
builder.Services.AddSqliteWasmBlazorCrypto(configure: o => o.BaseHref = baseHref);

// Short TTL keeps the PRF session-expiry E2E test fast. Post-auth ops in
// the other Facts complete inside 1-2s, so 5s leaves comfortable margin.
builder.Services.Configure<SqliteWasmBlazor.Crypto.Configuration.KeyCacheOptions>(o =>
{
    o.TtlMs = 5000;
});

var host = builder.Build();

// Initialize sqlite-wasm worker
await host.Services.InitializeSqliteWasmAsync();

// Boot setup is conditional on disk state. The on-disk passkey manifest is
// the source of truth for "is this VFS encrypted?" — wiping it (the old
// boot ResetDiskAsync did this) would orphan any persisted ciphertext
// across an F5 reload. Same story for the TodoDb EnsureDeletedAsync: a
// fresh plain-recreate of TodoDb in an otherwise-encrypted pool would
// break the disk-as-unit invariant. So we only do the destructive
// setup when the disk is genuinely Plain. Test runs see Plain at boot
// because each Playwright BrowserContext gets a fresh OPFS profile;
// interactive use across F5 sees Encrypted and we leave the disk alone.
{
    var session = host.Services.GetRequiredService<IEncryptedSqliteWasmDatabaseService>();
    var diskState = await session.GetStateAsync();
    if (!diskState.Encrypted)
    {
        using var scope = host.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TodoDbContext>>();
        await using var dbContext = await factory.CreateDbContextAsync();

        // Use EF Core migrations with custom SqliteWasmHistoryRepository
        // The custom history repository disables the infinite polling lock mechanism
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.MigrateAsync();

        Console.WriteLine("[TestApp] Plain disk — TodoDb recreated for a clean test run.");
    }
    else
    {
        Console.WriteLine(
            $"[TestApp] Disk is encrypted (manifest credentialId hint: '{diskState.Hint}'); " +
            "preserving content across reload — Authenticate to unlock.");
    }
}

await host.RunAsync();
