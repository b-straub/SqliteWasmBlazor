// SqliteWasmBlazor.CryptoSync - Boot integration with the typed status surface.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Boot-stage extensions for CryptoSync-enabled apps. Runs after
/// <c>InitializeSqliteWasmDatabaseAsync&lt;TContext&gt;</c> to verify that the
/// freshly migrated database is actually usable as a sync instance — admin
/// bootstrap completed, or member handshake completed, depending on the
/// device role declared in <see cref="DeviceSettings"/>.
/// </summary>
public static class CryptoSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the CryptoSync transport stack —
    /// <see cref="DeclarationSigner"/>, <see cref="IWhitelistPushService"/>,
    /// <see cref="IAdminPinService"/>, <see cref="IReceiveCursorStore"/>, and
    /// <see cref="ISyncTransport"/> — against the relay URL bound to
    /// <see cref="CryptoSyncOptions"/>.
    ///
    /// <para>
    /// <b>Caller responsibilities (Stage A test fixtures, Stage B production host).</b>
    /// The signer seams <see cref="ISenderAuthSigner"/> and
    /// <see cref="IReceiveAuthSigner"/> are <i>not</i> registered here — they're
    /// the host's identity contract. Stage A injects stub Ed25519 signers in
    /// xUnit fixtures; Stage B will register PRF/WebAuthn-backed implementations
    /// against the same seam without touching this method.
    /// </para>
    ///
    /// <para>
    /// <see cref="DeclarationSigner"/> depends on
    /// <see cref="Crypto.Abstractions.ICryptoProvider"/>, which the host registers
    /// via <c>AddSqliteWasmBlazorCrypto</c>. <see cref="HttpSyncTransport"/> and
    /// <see cref="WhitelistPushService"/> resolve <see cref="System.Net.Http.HttpClient"/>
    /// from the container — typically the scoped one Blazor WebAssembly hosts
    /// register against the app base address.
    /// </para>
    /// </summary>
    /// <typeparam name="TContext">Concrete domain context inheriting
    /// <see cref="CryptoSyncContextBase"/>. Used by
    /// <see cref="EfReceiveCursorStoreFactory{TContext}"/> to fetch a fresh
    /// context per receive-cursor read/write through
    /// <see cref="IDbContextFactory{TContext}"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Optional configuration root. When supplied,
    /// binds <see cref="CryptoSyncOptions.SectionName"/> from it.</param>
    /// <param name="configure">Optional callback to set
    /// <see cref="CryptoSyncOptions"/> programmatically (overlays the
    /// configuration binding).</param>
    public static IServiceCollection AddCryptoSync<TContext>(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<CryptoSyncOptions>? configure = null)
        where TContext : CryptoSyncContextBase
    {
        if (configuration is not null)
        {
            services.Configure<CryptoSyncOptions>(
                configuration.GetSection(CryptoSyncOptions.SectionName));
        }
        else
        {
            services.AddOptions<CryptoSyncOptions>();
        }

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddCryptoSyncCrypto();

        // Encrypted delta-bulk surface — wraps the now-internal worker
        // bridge methods (DeltaExport/DeltaImport/DeltaRotateKey) in a
        // CryptoSync-owned interface. Plain SQLite-on-OPFS hosts that don't
        // call AddCryptoSync never see this service.
        services.AddSingleton<Services.ICryptoSyncDeltaService, Services.CryptoSyncDeltaService>();

        services.AddSingleton<DeclarationSigner>();

        // Plane-3 domain services — interface-bound so CryptoSync.UI
        // ObservableModels (ContactsModel, InvitationModel, UserProfileModel)
        // inject the I* seam without seeing the internal impl class. The four
        // services take the consumer's CryptoSyncContextBase via EF Core's
        // Scoped DbContext resolution — hosts that wire a Scoped
        // CryptoSyncContextBase (or an alias from IDbContextFactory<TContext>)
        // get these for free. Tests bypass DI and construct the concrete
        // classes directly via the assembly's InternalsVisibleTo grant.
        services.AddScoped<Abstractions.IGroupService, GroupService>();
        services.AddScoped<Abstractions.IContactService, ContactService>();
        services.AddScoped<Abstractions.IDeviceIdentityService, DeviceIdentityService>();
        services.AddScoped<Abstractions.IContactInvitationService, ContactInvitationService>();
        // Protocol-op services not yet wired by a SyncOrchestrator caller;
        // registered now so consumer wire-up only has to inject the I* seam.
        services.AddScoped<Abstractions.ILeaveService, LeaveService>();
        services.AddScoped<Abstractions.IGroupTransferService, GroupTransferService>();
        services.AddScoped<Abstractions.ISyncGate, SyncGate>();
        services.AddScoped<Abstractions.ISharingService, SharingService>();

        services.AddScoped<IReceiveCursorStore>(sp =>
            new EfReceiveCursorStoreFactory<TContext>(
                sp.GetRequiredService<IDbContextFactory<TContext>>()));

        services.AddScoped<IWhitelistPushService>(sp => new WhitelistPushService(
            sp.GetRequiredService<HttpClient>(),
            ResolveRelayBaseUri(sp),
            sp.GetRequiredService<DeclarationSigner>()));

        services.AddScoped<IAdminPinService>(sp => new AdminPinService(
            sp.GetRequiredService<HttpClient>(),
            ResolveRelayBaseUri(sp),
            sp.GetRequiredService<DeclarationSigner>()));

        services.AddScoped<ISyncTransport>(sp => new HttpSyncTransport(
            sp.GetRequiredService<HttpClient>(),
            ResolveRelayBaseUri(sp),
            sp.GetRequiredService<ISenderAuthSigner>(),
            sp.GetRequiredService<IReceiveAuthSigner>(),
            sp.GetRequiredService<IReceiveCursorStore>()));

        return services;
    }

    /// <summary>
    /// Registers the CryptoSync-plane crypto services that layer on top of
    /// the base <see cref="ICryptoProvider"/> registered by
    /// <c>AddSqliteWasmBlazorCrypto</c>: <see cref="IGroupEncryption"/> +
    /// <see cref="IVapidCryptoProvider"/>. Use this when the consumer needs
    /// the crypto services but not the HTTP relay transport (e.g. test
    /// runners exercising group encryption without a real relay).
    /// </summary>
    /// <remarks>
    /// <see cref="AddCryptoSync{TContext}"/> calls this internally before
    /// registering the transport services, so callers using the full
    /// CryptoSync stack do not need to call this directly.
    /// </remarks>
    public static IServiceCollection AddCryptoSyncCrypto(this IServiceCollection services)
    {
        services.AddSingleton<IVapidCryptoProvider, VapidCryptoProvider>();
        services.AddSingleton<IGroupEncryption, GroupEncryptionService>();
        return services;
    }

    /// <summary>
    /// Registers production PRF-backed implementations of
    /// <see cref="ISenderAuthSigner"/> and <see cref="IReceiveAuthSigner"/>.
    /// Both delegate Ed25519 signing to <see cref="ISigningService"/>, which
    /// routes through the JS-side keyId cache so the priv never crosses the
    /// C#↔JS boundary.
    ///
    /// <para>
    /// <b>When to call:</b> Production hosts call this after
    /// <c>AddSqliteWasmBlazorCrypto</c> + <see cref="AddCryptoSync{TContext}"/>
    /// to opt in to PRF-driven HTTP relay auth. Test fixtures that want stub
    /// signers simply skip this call and register their stubs directly.
    /// Caller responsibility: a PRF session must be active (e.g. via
    /// <c>PrfService.DeriveKeysWithHintAsync</c>) before <see cref="HttpSyncTransport"/>
    /// touches either signer; otherwise <see cref="ISenderAuthSigner.OwnEd25519PublicKeyBase64"/>
    /// throws.
    /// </para>
    /// </summary>
    public static IServiceCollection AddCryptoSyncPrfSigners(this IServiceCollection services)
    {
        services.AddSingleton<ISenderAuthSigner, PrfBackedSenderAuthSigner>();
        services.AddSingleton<IReceiveAuthSigner, PrfBackedReceiveAuthSigner>();
        return services;
    }

    private static Uri ResolveRelayBaseUri(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<CryptoSyncOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.RelayBaseUri))
        {
            throw new InvalidOperationException(
                $"CryptoSync: '{nameof(CryptoSyncOptions.RelayBaseUri)}' is not configured. "
                + $"Bind '{CryptoSyncOptions.SectionName}:{nameof(CryptoSyncOptions.RelayBaseUri)}' "
                + "from configuration or pass a 'configure' callback to AddCryptoSync.");
        }
        return new Uri(options.RelayBaseUri, UriKind.Absolute);
    }

    /// <summary>
    /// Verifies the CryptoSync seed state of <typeparamref name="TContext"/>
    /// and reports the appropriate failure to the unified boot status. Intended
    /// for use in <c>Program.cs</c> after the library's migration helper:
    /// <code>
    /// await services.InitializeSqliteWasmDatabaseAsync&lt;TodoDbContext&gt;();
    /// await services.InitializeCryptoSyncAsync&lt;TodoDbContext&gt;();
    /// </code>
    /// Short-circuits if the prior boot stage already promoted the status away
    /// from <see cref="DbInitState.READY"/>.
    /// </summary>
    public static async ValueTask InitializeCryptoSyncAsync<TContext>(this IServiceProvider services)
        where TContext : CryptoSyncContextBase
    {
        var status = services.GetRequiredService<IDbInitializationStatus>();
        var reporter = services.GetRequiredService<IDbInitializationReporter>();

        if (status.State != DbInitState.READY)
        {
            return;
        }

        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
        await using var ctx = await factory.CreateDbContextAsync();

        await VerifyCryptoSyncSeedAsync(ctx, reporter, ExtractDatabaseName(ctx));
    }

    /// <summary>
    /// Runs the CryptoSync seed checks against an open context and routes the
    /// outcome through <paramref name="reporter"/>. Public so tests and apps
    /// composing custom boot pipelines can invoke the check without going
    /// through <see cref="IServiceProvider"/>.
    /// </summary>
    public static async ValueTask VerifyCryptoSyncSeedAsync(
        CryptoSyncContextBase ctx,
        IDbInitializationReporter reporter,
        string databaseName)
    {
        try
        {
            var device = await ctx.DeviceSettings.AsNoTracking().FirstOrDefaultAsync();
            if (device is null)
            {
                reporter.Report(DbInitState.FAILED, new DeviceNotProvisionedFailure(databaseName));
                return;
            }

            if (device.IsAdmin)
            {
                var hasSystemGroup = await ctx.ShareGroups.AsNoTracking()
                    .AnyAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
                if (!hasSystemGroup)
                {
                    reporter.Report(DbInitState.FAILED, new SystemSeedMissingFailure(databaseName));
                    return;
                }
            }
            else
            {
                var hasAdmin = await ctx.Contacts.AsNoTracking().AnyAsync(c => c.IsAdmin);
                if (!hasAdmin)
                {
                    reporter.Report(DbInitState.FAILED, new AdminContactMissingFailure(databaseName));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            reporter.Report(DbInitState.FAILED, new GenericInitFailure(databaseName, ex));
        }
    }

    private static string ExtractDatabaseName(DbContext ctx)
    {
        var connectionString = ctx.Database.GetDbConnection().ConnectionString;
        const string key = "Data Source=";
        var idx = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return ctx.GetType().Name;
        }

        var start = idx + key.Length;
        var end = connectionString.IndexOf(';', start);
        return end < 0
            ? connectionString[start..].Trim()
            : connectionString[start..end].Trim();
    }
}
