using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

/// <summary>
/// First-time WebAuthn credential creation. The host wires
/// <see cref="IPrfAuthenticator.RegisterAsync"/>; the model surfaces the
/// returned <see cref="CredentialId"/> + <see cref="PublicKey"/> as
/// observable properties so RxBlazor hosts can react via internal
/// observers or <see cref="ObservableModelObserverAttribute"/>. Errors
/// route through the injected <see cref="StatusModel"/> via the
/// per-command formatter.
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class RegistrationModel : ObservableModel
{
    public partial RegistrationModel(
        IPrfAuthenticator authenticator,
        StatusModel statusModel,
        IStringLocalizer<RegistrationModel> localizer);

    public partial bool? IsPrfSupported { get; set; }
    public partial string? DisplayName { get; set; }
    public partial string? CredentialId { get; set; }
    public partial string? PublicKey { get; set; }

    [ObservableCommand(nameof(CheckPrfSupportAsync))]
    public partial IObservableCommandAsync CheckPrfSupport { get; }

    [ObservableCommand(nameof(RegisterAsync), nameof(CanRegister), nameof(FormatRegisterError))]
    public partial IObservableCommandAsync Register { get; }

    private bool CanRegister() => IsPrfSupported == true;

    private async Task CheckPrfSupportAsync()
    {
        // Pessimistic init — if the probe throws, IsPrfSupported stays false
        // and the panel renders the "PRF not supported" branch.
        IsPrfSupported = false;
        IsPrfSupported = await Authenticator.CheckPrfSupportAsync();
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var hint = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName;
        var result = await Authenticator.RegisterAsync(hint, cancellationToken);
        CredentialId = result.CredentialId;
        PublicKey = result.PublicKeyBase64;
        DisplayName = null;
        StatusModel.AddSuccess(
            Localizer["Status_Registered"],
            nameof(Register));
    }

    private string FormatRegisterError(Exception ex) =>
        Localizer["Error_Register", ex.Message];
}
