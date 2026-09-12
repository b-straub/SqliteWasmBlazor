using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqliteWasmBlazor.Crypto.UI.Abstractions;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI;

/// <summary>
/// Host-side DI registration for <c>SqliteWasmBlazor.Crypto.UI</c>, the
/// base-plane Razor library carved out of <c>SqliteWasmBlazor.CryptoSync.UI</c>
/// in plane-separation Phase 1.1. Hosts that only need the base-plane
/// surface (encrypted VFS via PRF, boot-status, session re-auth) call
/// <see cref="AddCryptoUI"/>; hosts that also need delta-sync / contacts /
/// invitations / push call
/// <c>CryptoSyncUiServiceCollectionExtensions.AddCryptoSyncUI</c>
/// in <c>SqliteWasmBlazor.CryptoSync.UI</c>, which calls
/// <see cref="AddCryptoUI"/> first.
///
/// <para>
/// Registers the <see cref="ServiceLifetime.Scoped"/> <c>ObservableModel</c>
/// instances backing each base-plane panel
/// (<see cref="Components.Authentication.AuthenticationModel"/>,
/// <see cref="Components.Shared.DatabaseInformationAlertModel"/>,
/// <see cref="Components.Shared.SessionExpiredPopoverModel"/>) plus the
/// singleton <see cref="RxBlazorV2.MudBlazor.Components.StatusModel"/>
/// status sink every command in this library routes to. Hosts render
/// <c>&lt;RxBlazorV2.MudBlazor.Components.Razor.StatusDisplay/&gt;</c>
/// in their layout to surface those messages.
/// </para>
///
/// <para>
/// <b>Caller responsibilities.</b> The host registers the host-supplied
/// seams separately — <see cref="AddCryptoUI"/> deliberately does not
/// touch them so a non-WebAuthn host (e.g. test fixture) can wire stubs:
/// <list type="bullet">
///   <item><see cref="Services.IPrfAuthenticator"/> — backs the
///         <see cref="Components.Authentication.AuthenticationPanel"/>.
///         Production impl arrives via
///         <see cref="AddCryptoUIPrfAuthenticator"/>.</item>
///   <item><see cref="IHostRecoveryService"/> — which databases the app
///         owns, how to migrate them, and how to recover from a broken
///         boot. Register with <see cref="AddHostRecoveryService{THost}"/>;
///         hosts that ship no reset path register
///         <see cref="NullHostRecoveryService.Instance"/>.</item>
///   <item><see cref="Services.ISessionAuthenticator"/> — backs the
///         re-authenticate / dismiss flow on
///         <see cref="Components.Shared.SessionExpiredPopover"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Localization.</b> Each panel-backing model resolves
/// <see cref="Microsoft.Extensions.Localization.IStringLocalizer{T}"/> for
/// its user-facing strings. The host MUST call
/// <c>services.AddLocalization()</c> and SHOULD set
/// <c>&lt;BlazorWebAssemblyLoadAllGlobalizationData&gt;true&lt;/&gt;</c> in
/// its csproj so the WASM runtime ships every satellite resource assembly
/// and respects <c>navigator.language</c> at boot.
/// </para>
/// </summary>
public static class CryptoUiServiceCollectionExtensions
{
    /// <summary>
    /// Register every panel-backing model exposed by
    /// <c>SqliteWasmBlazor.Crypto.UI</c> plus the
    /// <see cref="RxBlazorV2.MudBlazor.Components.StatusModel"/> singleton
    /// the library's commands route exceptions and status messages to.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddCryptoUI(this IServiceCollection services)
    {
        ObservableModels.Initialize(services);
        RxBlazorV2.MudBlazor.ObservableModels.Initialize(services);

        // PrfAuthenticationStateProvider is the single source of truth for
        // "is a PRF session active?" — registered both as itself (so the
        // panel-backing AuthenticationModel can inject it via partial
        // ctor) and as Blazor's standard AuthenticationStateProvider seam
        // (so consumer hosts get <AuthorizeView> + [CascadingParameter]
        // Task<AuthenticationState> for free, no hand-rolled R3
        // subscriptions in page partials).
        services.AddAuthorizationCore(options =>
        {
            // DatabaseOpen policy — gates pages that touch the DB. Satisfied
            // when the boot DB state is READY (plain DB OR encrypted DB with
            // worker K installed). NotAuthorized branch typically renders
            // <AuthenticationPanel/> so the user can sign in to unlock an
            // encrypted DB; once EncryptedDatabaseLifecycle promotes state
            // back to READY, the AuthorizeView flips automatically.
            options.AddPolicy("DatabaseOpen", policy =>
                policy.RequireClaim(
                    PrfAuthenticationStateProvider.DatabaseStateClaim,
                    PrfAuthenticationStateProvider.DatabaseStateOpen));
        });
        services.AddSingleton<PrfAuthenticationStateProvider>();
        services.AddSingleton<AuthenticationStateProvider>(
            sp => sp.GetRequiredService<PrfAuthenticationStateProvider>());
        services.AddSingleton<IPrfAuthenticationStateProvider>(
            sp => sp.GetRequiredService<PrfAuthenticationStateProvider>());

        // Replace the base-package DbInitializationService with the
        // reactive DbStateModel. ObservableModels.Initialize already
        // registered DbStateModel as a Singleton; bind the IDbInitialization*
        // interfaces to it so base-package writers (EncryptedSqliteWasmDatabaseService,
        // ISqliteWasmInitializer) push state through the model's
        // partial-property pipeline. Hosts that don't reference Crypto.UI
        // keep the plain DbInitializationService and don't get reactivity.
        services.Replace(ServiceDescriptor.Singleton<IDbInitializationStatus>(
            sp => sp.GetRequiredService<DbStateModel>()));
        services.Replace(ServiceDescriptor.Singleton<IDbInitializationReporter>(
            sp => sp.GetRequiredService<DbStateModel>()));

        return services;
    }

    /// <summary>
    /// Eagerly resolve <see cref="EncryptedPoolLifecycle"/> + <see cref="DbStateModel"/>
    /// so their auto-detected observers + R3 subscriptions are wired before
    /// the first page render. Call after <c>builder.Build()</c> in
    /// <c>Program.cs</c>; if skipped, the very first auth event or boot
    /// state push might fire before the singletons are constructed and the
    /// observer wiring would miss it.
    /// </summary>
    public static IServiceProvider UseEncryptedPoolLifecycle(this IServiceProvider services)
    {
        _ = services.GetRequiredService<DbStateModel>();
        _ = services.GetRequiredService<EncryptedPoolLifecycle>();
        return services;
    }

    /// <summary>
    /// Opt-in registration of the production <see cref="IPrfAuthenticator"/>
    /// implementation backed by the base-plane <see cref="Crypto.Services.IPrfService"/>.
    /// Hosts that ship a real WebAuthn-PRF UX (the demo, downstream consumer
    /// apps) call this after <c>AddSqliteWasmBlazorCrypto</c> to wire the seam
    /// consumed by <see cref="Components.Authentication.AuthenticationPanel"/>;
    /// test fixtures
    /// that want a stub skip this call and register their own
    /// <see cref="IPrfAuthenticator"/>. Mirrors the
    /// <c>AddCryptoSyncPrfSigners</c> shape from
    /// <c>SqliteWasmBlazor.CryptoSync</c>.
    ///
    /// <para>
    /// Registered as <see cref="ServiceLifetime.Scoped"/> so it composes with
    /// either base-plane <see cref="Crypto.Services.IPrfService"/> registration
    /// (singleton via the
    /// <c>AddSqliteWasmBlazorCrypto(IConfiguration?, ...)</c> overload, scoped
    /// via the <c>AddSqliteWasmBlazorCrypto(Action&lt;PrfOptions&gt;, ...)</c>
    /// overload).
    /// </para>
    /// </summary>
    public static IServiceCollection AddCryptoUIPrfAuthenticator(this IServiceCollection services)
    {
        services.AddSingleton<IPrfAuthenticator, PrfAuthenticator>();
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="THost"/> as the host seam, bound to
    /// both interfaces it satisfies: <see cref="IHostRecoveryService"/>,
    /// which these panels resolve for the reset affordance, and
    /// <see cref="IHostDatabaseService"/>, which the base plane's import
    /// paths consult for owned-database names and the schema gate. One
    /// class, one call, one instance behind both.
    /// </summary>
    /// <remarks>
    /// A singleton, because the base plane's consumers of it are — see
    /// <c>AddHostDatabaseService</c>. The panels that inject it are scoped,
    /// which is fine in that direction.
    /// </remarks>
    /// <typeparam name="THost">The host's implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHostRecoveryService<THost>(
        this IServiceCollection services)
        where THost : class, IHostRecoveryService
    {
        services.AddSingleton<THost>();
        services.AddSingleton<IHostRecoveryService>(sp => sp.GetRequiredService<THost>());
        services.AddSingleton<IHostDatabaseService>(sp => sp.GetRequiredService<THost>());
        return services;
    }
}
