using Microsoft.Extensions.DependencyInjection;

namespace SqliteWasmBlazor.Crypto.UI;

/// <summary>
/// Host-side DI registration for <c>SqliteWasmBlazor.Crypto.UI</c>, the
/// base-plane Razor library carved out of <c>SqliteWasmBlazor.CryptoSync.UI</c>
/// in plane-separation Phase 1.1. Hosts that only need the base-plane
/// surface (encrypted VFS via PRF, boot-status, session re-auth) call
/// <see cref="AddCryptoUI"/>; hosts that also need delta-sync / contacts /
/// invitations / push call
/// <see cref="UI.CryptoSyncUiServiceCollectionExtensions.AddCryptoSyncUI"/>
/// in <c>SqliteWasmBlazor.CryptoSync.UI</c>, which calls
/// <see cref="AddCryptoUI"/> first.
///
/// <para>
/// Registers the <see cref="ServiceLifetime.Scoped"/> <c>ObservableModel</c>
/// instances backing each base-plane panel
/// (<see cref="Components.Authentication.AuthenticationModel"/>,
/// <see cref="Components.Authentication.RegistrationModel"/>,
/// <see cref="Components.Shared.DatabaseErrorAlertModel"/>,
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
///         <see cref="Components.Authentication.AuthenticationPanel"/> and
///         <see cref="Components.Authentication.RegistrationPanel"/>.
///         Production impl arrives in plane-separation Phase 3.</item>
///   <item><see cref="Services.IDatabaseResetService"/> — boot-status
///         recovery callback; register
///         <see cref="Services.NullDatabaseResetService.Instance"/> for
///         hosts that don't ship a reset path.</item>
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
        return services;
    }
}
