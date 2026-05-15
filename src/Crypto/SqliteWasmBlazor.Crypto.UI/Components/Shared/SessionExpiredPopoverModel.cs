using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Shared;

/// <summary>
/// Backing model for <see cref="SessionExpiredPopover"/>. The host writes
/// <see cref="Visible"/> = <c>true</c> when its session-expiry detection
/// fires; the popover renders the overlay until one of the two commands
/// runs, both of which clear <see cref="Visible"/>.
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class SessionExpiredPopoverModel : ObservableModel
{
    public partial SessionExpiredPopoverModel(
        ISessionAuthenticator authenticator,
        StatusModel statusModel,
        IStringLocalizer<SessionExpiredPopoverModel> localizer);

    public partial bool Visible { get; set; }

    [ObservableCommand(nameof(ReAuthenticateAsync), null, nameof(FormatReAuthenticateError))]
    public partial IObservableCommandAsync ReAuthenticate { get; }

    [ObservableCommand(nameof(DismissAsync), null, nameof(FormatDismissError))]
    public partial IObservableCommandAsync Dismiss { get; }

    private async Task ReAuthenticateAsync(CancellationToken cancellationToken)
    {
        await Authenticator.ReAuthenticateAsync(cancellationToken);
        Visible = false;
    }

    private async Task DismissAsync(CancellationToken cancellationToken)
    {
        await Authenticator.DismissAsync(cancellationToken);
        Visible = false;
    }

    private string FormatReAuthenticateError(Exception ex) =>
        Localizer["Error_ReAuth", ex.Message];

    private string FormatDismissError(Exception ex) =>
        Localizer["Error_Dismiss", ex.Message];
}
