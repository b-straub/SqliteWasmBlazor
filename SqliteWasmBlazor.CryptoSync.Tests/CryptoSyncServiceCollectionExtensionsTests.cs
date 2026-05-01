using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.BouncyCastle;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Coverage for <c>AddCryptoSync&lt;TContext&gt;</c> in
/// <see cref="CryptoSyncServiceCollectionExtensions"/>: verifies the DI shape
/// the Stage A scenario sweep + Stage B browser host depend on. The signer
/// seams (<see cref="ISenderAuthSigner"/> / <see cref="IReceiveAuthSigner"/>)
/// are deliberately registered by the caller — Stage A injects the stubs
/// from <c>Fixtures/</c>, Stage B will register PRF-backed implementations
/// against the same seam.
/// </summary>
public sealed class CryptoSyncServiceCollectionExtensionsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TestSyncContext> _dbOptions;

    public CryptoSyncServiceCollectionExtensionsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;
        using var seed = new TestSyncContext(_dbOptions);
        seed.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private ServiceProvider BuildHost(
        Action<CryptoSyncOptions>? configure = null,
        bool registerSigners = true)
    {
        var services = new ServiceCollection();

        services.AddSingleton<HttpClient>(_ => new HttpClient());
        services.AddSingleton<ICryptoProvider>(_ => new BouncyCastleCryptoProvider());

        services.AddDbContextFactory<TestSyncContext>(opts => opts.UseSqlite(_connection));

        if (registerSigners)
        {
            services.AddSingleton<ISenderAuthSigner>(_ => new StubSenderAuthSigner
            {
                OwnEd25519PublicKeyBase64 = "AAAA",
            });
            services.AddSingleton<IReceiveAuthSigner>(_ => new StubReceiveAuthSigner
            {
                OwnEd25519PublicKeyBase64 = "BBBB",
            });
        }

        services.AddCryptoSync<TestSyncContext>(configuration: null, configure);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCryptoSync_ConfigureCallback_RegistersTransportStack()
    {
        using var host = BuildHost(o => o.RelayBaseUri = "http://relay.test/");

        Assert.NotNull(host.GetRequiredService<DeclarationSigner>());
        Assert.NotNull(host.GetRequiredService<IWhitelistPushService>());
        Assert.NotNull(host.GetRequiredService<IAdminPinService>());
        Assert.NotNull(host.GetRequiredService<IReceiveCursorStore>());
        Assert.NotNull(host.GetRequiredService<ISyncTransport>());
    }

    [Fact]
    public void AddCryptoSync_TransportIsHttpSyncTransport()
    {
        using var host = BuildHost(o => o.RelayBaseUri = "http://relay.test/");

        var transport = host.GetRequiredService<ISyncTransport>();
        Assert.IsType<HttpSyncTransport>(transport);
    }

    [Fact]
    public void AddCryptoSync_PushServiceIsWhitelistPushService()
    {
        using var host = BuildHost(o => o.RelayBaseUri = "http://relay.test/");

        var pusher = host.GetRequiredService<IWhitelistPushService>();
        Assert.IsType<WhitelistPushService>(pusher);
    }

    [Fact]
    public void AddCryptoSync_PinServiceIsAdminPinService()
    {
        using var host = BuildHost(o => o.RelayBaseUri = "http://relay.test/");

        var pinner = host.GetRequiredService<IAdminPinService>();
        Assert.IsType<AdminPinService>(pinner);
    }

    [Fact]
    public void AddCryptoSync_CursorStoreIsFactoryWrapper()
    {
        using var host = BuildHost(o => o.RelayBaseUri = "http://relay.test/");

        var store = host.GetRequiredService<IReceiveCursorStore>();
        Assert.IsType<EfReceiveCursorStoreFactory<TestSyncContext>>(store);
    }

    [Fact]
    public async Task AddCryptoSync_CursorStore_RoundTripsThroughDbContextFactory()
    {
        // Tests the production wiring end-to-end: AddDbContextFactory →
        // EfReceiveCursorStoreFactory → fresh context per call → EF row.
        // Proves a fresh process-scope worth of registrations could persist
        // and recall a cursor without the test pre-creating contexts.
        using var host = BuildHost(o => o.RelayBaseUri = "http://relay.test/");

        var store = host.GetRequiredService<IReceiveCursorStore>();

        Assert.Equal(0L, await store.LoadAsync());

        await store.SaveAsync(7777L);

        Assert.Equal(7777L, await store.LoadAsync());
    }

    [Fact]
    public void AddCryptoSync_RelayUriUnset_ResolveThrowsActionable()
    {
        using var host = BuildHost(configure: null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => host.GetRequiredService<ISyncTransport>());
        Assert.Contains(nameof(CryptoSyncOptions.RelayBaseUri), ex.Message);
        Assert.Contains(CryptoSyncOptions.SectionName, ex.Message);
    }

    [Fact]
    public void AddCryptoSync_DoesNotRegisterSignerSeams()
    {
        // Stage A test fixtures + Stage B host both register their own
        // ISenderAuthSigner / IReceiveAuthSigner. AddCryptoSync must leave
        // these seams open — registering them here would defeat the swap.
        using var host = BuildHost(
            o => o.RelayBaseUri = "http://relay.test/",
            registerSigners: false);

        Assert.Throws<InvalidOperationException>(
            () => host.GetRequiredService<ISyncTransport>());
    }
}
