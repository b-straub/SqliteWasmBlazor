using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Models;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

/// <summary>
/// Drives the authentication / key-derivation panel. Owns the derived
/// public key, optional metadata, the metadata-edit dialog state, and
/// the copy-to-clipboard signal. RxBlazor hosts that need to react to a
/// successful key derivation observe <see cref="CredentialId"/> +
/// <see cref="PublicKey"/> on this model directly.
///
/// <para>
/// <b>Error / status routing.</b> The injected <see cref="StatusBaseModel"/>
/// is the centralized sink: command exceptions are auto-routed there
/// through the per-command formatter (third <c>[ObservableCommand]</c>
/// argument); explicit graceful-flow status (cancellation, success) is
/// pushed via <see cref="StatusBaseModel.AddWarning"/> /
/// <see cref="StatusBaseModel.AddSuccess"/> with a <c>source</c> tag.
/// The host renders a <c>&lt;StatusDisplay/&gt;</c> shell-level component
/// that consumes that sink.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class AuthenticationModel : ObservableModel
{
    public partial AuthenticationModel(
        IPrfAuthenticator authenticator,
        StatusModel statusModel,
        IStringLocalizer<AuthenticationModel> localizer);

    public partial string? CredentialId { get; set; }
    public partial string? PublicKey { get; set; }
    public partial PublicKeyMetadata? Metadata { get; set; }
    public partial bool? IsPrfSupported { get; set; }

    public partial bool DialogVisible { get; set; }
    public partial string? EditName { get; set; }
    public partial string? EditEmail { get; set; }
    public partial string? EditComment { get; set; }

    /// <summary>
    /// Copy-to-clipboard signal. The component-trigger override does the
    /// JS interop + snackbar, then clears the signal so a subsequent click
    /// re-fires the trigger.
    /// </summary>
    [ObservableComponentTriggerAsync]
    public partial string? PendingCopy { get; set; }

    public bool HasKeys => !string.IsNullOrEmpty(PublicKey);
    public bool IsExecuting => DeriveKeys.Executing;

    [ObservableCommand(nameof(CheckPrfSupportAsync))]
    public partial IObservableCommandAsync CheckPrfSupport { get; }

    [ObservableCommand(nameof(DeriveKeysAsync), null, nameof(FormatDeriveKeysError))]
    public partial IObservableCommandAsync DeriveKeys { get; }

    [ObservableCommand(nameof(ClearKeys))]
    public partial IObservableCommand ClearKeysCommand { get; }

    [ObservableCommand(nameof(OpenMetadataDialog), nameof(CanInteractWithKey))]
    public partial IObservableCommand OpenMetadataDialogCommand { get; }

    [ObservableCommand(nameof(CancelMetadataDialog))]
    public partial IObservableCommand CancelMetadataDialogCommand { get; }

    [ObservableCommand(nameof(SaveMetadata))]
    public partial IObservableCommand SaveMetadataCommand { get; }

    [ObservableCommand(nameof(RequestCopy), nameof(CanInteractWithKey))]
    public partial IObservableCommand RequestCopyCommand { get; }

    private bool CanInteractWithKey() => HasKeys;

    private async Task CheckPrfSupportAsync()
    {
        // Pessimistic init — if the probe throws, IsPrfSupported stays false
        // and the panel renders the "PRF not supported" branch.
        IsPrfSupported = false;
        IsPrfSupported = await Authenticator.CheckPrfSupportAsync();
    }

    private async Task DeriveKeysAsync(CancellationToken cancellationToken)
    {
        var hint = string.IsNullOrWhiteSpace(CredentialId) ? null : CredentialId;
        var result = await Authenticator.AuthenticateAsync(hint, cancellationToken);
        if (result is null)
        {
            StatusModel.AddWarning(
                Localizer["Status_AuthenticationCancelled"],
                nameof(DeriveKeys));
            return;
        }
        CredentialId = result.CredentialId;
        PublicKey = result.PublicKeyBase64;
        StatusModel.AddSuccess(
            Localizer["Status_KeysDerived"],
            nameof(DeriveKeys));
    }

    private void ClearKeys()
    {
        PublicKey = null;
        Metadata = null;
    }

    private void OpenMetadataDialog()
    {
        EditName = Metadata?.Name;
        EditEmail = Metadata?.Email;
        EditComment = Metadata?.Comment;
        DialogVisible = true;
    }

    private void CancelMetadataDialog()
    {
        DialogVisible = false;
    }

    private void SaveMetadata()
    {
        var hasData = !string.IsNullOrWhiteSpace(EditName)
            || !string.IsNullOrWhiteSpace(EditEmail)
            || !string.IsNullOrWhiteSpace(EditComment);

        Metadata = hasData
            ? new PublicKeyMetadata
            {
                Name = string.IsNullOrWhiteSpace(EditName) ? null : EditName.Trim(),
                Email = string.IsNullOrWhiteSpace(EditEmail) ? null : EditEmail.Trim(),
                Comment = string.IsNullOrWhiteSpace(EditComment) ? null : EditComment.Trim(),
                Created = Metadata?.Created ?? DateOnly.FromDateTime(DateTime.Today),
            }
            : null;

        DialogVisible = false;
    }

    private void RequestCopy()
    {
        PendingCopy = PublicKey;
    }

    private string FormatDeriveKeysError(Exception ex) =>
        Localizer["Error_DeriveKeys", ex.Message];
}
