using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using BlazorPRF.Crypto.Abstractions.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// One CryptoSync participant in a test scenario — admin OR a regular user.
/// Each actor owns its own in-memory SQLite database, its own
/// <see cref="TestSyncContext"/>, its own keypair, and a fully-wired set of
/// services so test code can call them exactly the way a real consumer would
/// (no raw <c>DbContext</c> writes from test bodies).
///
/// Two actors operating against the same shared in-memory connection would
/// see each other's writes, which would defeat the point of testing sync.
/// Each actor here gets its OWN <see cref="SqliteConnection"/> backed by a
/// separate <c>DataSource=:memory:</c> instance.
/// </summary>
public sealed class TestActor : IAsyncDisposable
{
    public string Name { get; }
    public bool IsAdmin { get; }
    public DualKeyPairFull Keys { get; }
    public ICryptoProvider Crypto { get; }
    public TestSyncContext Context { get; }

    public ContactService Contacts { get; }
    public DeviceIdentityService DeviceIdentity { get; }
    public CryptoSyncBootstrap Bootstrap { get; }
    public SyncGate Gate { get; }

    private readonly SqliteConnection _connection;

    private TestActor(
        string name,
        bool isAdmin,
        DualKeyPairFull keys,
        ICryptoProvider crypto,
        SqliteConnection connection,
        TestSyncContext context)
    {
        Name = name;
        IsAdmin = isAdmin;
        Keys = keys;
        Crypto = crypto;
        _connection = connection;
        Context = context;

        DeviceIdentity = new DeviceIdentityService(context);
        Contacts = new ContactService(context);
        Bootstrap = new CryptoSyncBootstrap(new GroupEncryptionService(crypto));
        Gate = new SyncGate(Contacts);
    }

    /// <summary>
    /// Build a fresh actor: open an in-memory SQLite connection, create a
    /// brand-new <see cref="TestSyncContext"/> against it, derive a deterministic
    /// keypair from a fixed seed (so test runs are reproducible), wire all the
    /// services. The caller is responsible for any subsequent bootstrap or
    /// contact-population steps — this method does NOT call
    /// <see cref="CryptoSyncBootstrap.InitializeAdminAsync"/>.
    /// </summary>
    /// <param name="name">Display name (used for the keypair seed).</param>
    /// <param name="isAdmin">Whether this actor will eventually be the admin
    /// of the network. Doesn't itself flip <c>IsAdmin</c> on DeviceSettings —
    /// that happens during bootstrap.</param>
    /// <param name="seedByte">First byte of the deterministic key seed; the
    /// remaining 31 bytes are filled with <c>seedByte + i</c>. Pass distinct
    /// values per actor to get distinct keypairs.</param>
    public static async Task<TestActor> CreateAsync(
        string name,
        bool isAdmin,
        byte seedByte,
        ICryptoProvider crypto)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(connection)
            .Options;
        var context = new TestSyncContext(options);
        await context.Database.EnsureCreatedAsync();

        // Derive a deterministic keypair from a seed for reproducible tests.
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(seedByte + i);
        }
        var keys = await crypto.DeriveDualKeyPairAsync(seed);

        return new TestActor(name, isAdmin, keys, crypto, connection, context);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
