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

        // CryptoSync-plane crypto services layered on top of the base
        // ICryptoProvider registered by AddSqliteWasmBlazorCrypto.
        services.AddSingleton<IVapidCryptoProvider, VapidCryptoProvider>();
        services.AddSingleton<IGroupEncryption, GroupEncryptionService>();

        services.AddSingleton<DeclarationSigner>();

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
