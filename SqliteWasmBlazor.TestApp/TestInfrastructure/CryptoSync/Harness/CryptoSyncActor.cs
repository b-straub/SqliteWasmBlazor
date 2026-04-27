using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync.Harness;

/// <summary>
/// One actor in a multi-actor CryptoSync integration scenario.
/// </summary>
internal sealed class CryptoSyncActor : IAsyncDisposable
{
    public string Name { get; }
    public string DatabaseName { get; }
    public bool IsAdmin { get; }
    public DualKeyPairFull Keys { get; }

    public CryptoTestContext Context { get; }

    public DeviceIdentityService DeviceIdentity { get; }
    public ContactService Contacts { get; }
    public SyncOrchestrator Sync { get; }

    private CryptoSyncActor(
        string name,
        string databaseName,
        bool isAdmin,
        DualKeyPairFull keys,
        CryptoTestContext context,
        DeviceIdentityService deviceIdentity,
        ContactService contacts,
        SyncOrchestrator sync)
    {
        Name = name;
        DatabaseName = databaseName;
        IsAdmin = isAdmin;
        Keys = keys;
        Context = context;
        DeviceIdentity = deviceIdentity;
        Contacts = contacts;
        Sync = sync;
    }

    public static async Task<CryptoSyncActor> CreateAsync(
        string name,
        bool isAdmin,
        ICryptoProvider crypto,
        ISqliteWasmDatabaseService databaseService)
    {
        var databaseName = $"{name.ToLowerInvariant()}-crypto.db";

        var connection = new SqliteWasmConnection($"Data Source={databaseName}");
        var optionsBuilder = new DbContextOptionsBuilder<CryptoTestContext>();
        optionsBuilder.UseSqliteWasm(connection);
        var context = new CryptoTestContext(optionsBuilder.Options);

        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        var keys = await crypto.DeriveDualKeyPairAsync(seed);

        var settings = new DeviceSettings
        {
            Id = Guid.NewGuid(),
            ClientGuid = Guid.NewGuid().ToString(),
            DeviceName = name,
            IsAdmin = isAdmin
        };
        context.DeviceSettings.Add(settings);
        await context.SaveChangesAsync();

        var deviceIdentity = new DeviceIdentityService(context);
        var contacts = new ContactService(context);
        var sync = new SyncOrchestrator(databaseService, context);

        return new CryptoSyncActor(
            name, databaseName, isAdmin, keys, context,
            deviceIdentity, contacts, sync);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
    }
}
