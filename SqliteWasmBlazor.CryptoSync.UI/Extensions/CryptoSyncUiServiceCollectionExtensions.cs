using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.Crypto.UI;

namespace SqliteWasmBlazor.CryptoSync.UI;

/// <summary>
/// Host-side DI registration for <c>SqliteWasmBlazor.CryptoSync.UI</c>, the
/// sync-plane Razor library hosting the panels that depend on the
/// CryptoSync surface (delta sync + contacts + invitations + push).
/// Call after <c>AddCryptoSync&lt;TContext&gt;</c> on the host's
/// <see cref="IServiceCollection"/>.
///
/// <para>
/// <see cref="AddCryptoSyncUI"/> calls
/// <see cref="Crypto.UI.CryptoUiServiceCollectionExtensions.AddCryptoUI"/>
/// first to register the base-plane panels (Authentication / Registration /
/// DatabaseErrorAlert / SessionExpiredPopover) and the
/// <see cref="RxBlazorV2.MudBlazor.Components.StatusModel"/> singleton, then
/// adds the sync-only models
/// (<see cref="Components.Profile.UserProfileModel"/>,
/// <see cref="Components.Contacts.ContactsModel"/>,
/// <see cref="Components.Invitation.InvitationModel"/>) at
/// <see cref="ServiceLifetime.Scoped"/>.
/// </para>
///
/// <para>
/// <b>Caller responsibilities.</b> Beyond the seams listed in
/// <see cref="Crypto.UI.CryptoUiServiceCollectionExtensions"/>, the host
/// also wires:
/// <list type="bullet">
///   <item><see cref="Services.IAdminInvitationContext"/> — supplied per
///         page or as a <c>[CascadingParameter]</c>; the
///         <see cref="Components.Invitation.InvitationPanel"/> renders an
///         informational placeholder when not supplied.</item>
///   <item><see cref="ISenderAuthSigner"/>, <see cref="IReceiveAuthSigner"/>,
///         <see cref="IPushNotifier"/> — the CryptoSync transport seams.
///         The library does not register defaults; the host wires them
///         (and may register <see cref="NullPushNotifier.Instance"/> as a
///         no-op for hosts that don't ship push).</item>
/// </list>
/// </para>
/// </summary>
public static class CryptoSyncUiServiceCollectionExtensions
{
    /// <summary>
    /// Register every panel-backing model exposed by
    /// <c>SqliteWasmBlazor.CryptoSync.UI</c> on top of the base-plane
    /// registrations from
    /// <see cref="Crypto.UI.CryptoUiServiceCollectionExtensions.AddCryptoUI"/>.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddCryptoSyncUI(this IServiceCollection services)
    {
        services.AddCryptoUI();
        ObservableModels.Initialize(services);
        return services;
    }
}
