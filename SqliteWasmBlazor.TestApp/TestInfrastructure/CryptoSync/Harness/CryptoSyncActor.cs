using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync.Harness;

/// <summary>
/// One actor in a multi-actor CryptoSync integration scenario — represents a
/// complete instance (device) with its own OPFS database, its own
/// <see cref="DeviceSettings"/> row, its own key pair, and its own fully-wired
/// set of CryptoSync services.
///
/// <para>
/// Actors never share a <see cref="CryptoTestContext"/> or a database name.
/// The harness creates (typically) three actors — Alice (admin), Bob (Editor),
/// Tom (Viewer) — and runs scenarios against them through the canonical service
/// API (no raw <c>DbContext</c> writes in test bodies).
/// </para>
/// </summary>
internal sealed class CryptoSyncActor : IAsyncDisposable
{
    public string Name { get; }
    public string DatabaseName { get; }
    public bool IsAdmin { get; }
    public DualKeyPairFull Keys { get; }

    public CryptoTestContext Context { get; }

    // Wired services — each scenario calls these directly, exactly the way a
    // future UI would.
    public DeviceIdentityService DeviceIdentity { get; }
    public ContactService Contacts { get; }
    public InvitationService Invitations { get; }
    public ContactPromotionService Promotion { get; }
    public SyncOrchestrator Sync { get; }

    private CryptoSyncActor(
        string name,
        string databaseName,
        bool isAdmin,
        DualKeyPairFull keys,
        CryptoTestContext context,
        DeviceIdentityService deviceIdentity,
        ContactService contacts,
        InvitationService invitations,
        ContactPromotionService promotion,
        SyncOrchestrator sync)
    {
        Name = name;
        DatabaseName = databaseName;
        IsAdmin = isAdmin;
        Keys = keys;
        Context = context;
        DeviceIdentity = deviceIdentity;
        Contacts = contacts;
        Invitations = invitations;
        Promotion = promotion;
        Sync = sync;
    }

    /// <summary>
    /// Boot a fresh actor: wipe and recreate its OPFS database, derive key pair,
    /// seed <see cref="DeviceSettings"/> (with <c>IsAdmin</c> set as requested),
    /// wire services.
    /// </summary>
    public static async Task<CryptoSyncActor> CreateAsync(
        string name,
        bool isAdmin,
        ICryptoProvider crypto,
        ISqliteWasmDatabaseService databaseService)
    {
        var databaseName = $"{name.ToLowerInvariant()}-crypto.db";

        // Per-actor DbContext with its own connection/database name.
        var connection = new SqliteWasmConnection($"Data Source={databaseName}");
        var optionsBuilder = new DbContextOptionsBuilder<CryptoTestContext>();
        optionsBuilder.UseSqliteWasm(connection);
        var context = new CryptoTestContext(optionsBuilder.Options);

        // Fresh database — each harness run starts from zero.
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        // Derive keys for this actor. Random seed per test run — not reproducible
        // across runs, but each run is internally consistent.
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        var keys = await crypto.DeriveDualKeyPairAsync(seed);

        // Seed the singleton DeviceSettings row. IsAdmin is the only flag that
        // gates admin-only operations (decision §12).
        var settings = new DeviceSettings
        {
            Id = Guid.NewGuid(),
            ClientGuid = Guid.NewGuid().ToString(),
            DeviceName = name,
            IsAdmin = isAdmin
        };
        context.DeviceSettings.Add(settings);
        await context.SaveChangesAsync();

        // Wire services in the exact shape Phase I's AddCryptoSync will register.
        var deviceIdentity = new DeviceIdentityService(context);
        var contacts = new ContactService(context);
        var invitations = new InvitationService(context, deviceIdentity);
        var promotion = new ContactPromotionService(context, deviceIdentity);
        var sync = new SyncOrchestrator(databaseService, crypto, contacts);

        return new CryptoSyncActor(
            name, databaseName, isAdmin, keys, context,
            deviceIdentity, contacts, invitations, promotion, sync);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
    }
}
