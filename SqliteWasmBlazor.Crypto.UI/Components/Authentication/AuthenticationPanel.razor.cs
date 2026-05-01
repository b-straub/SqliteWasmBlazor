using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

public partial class AuthenticationPanel
{
    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ISnackbar Snackbar { get; init; }

    /// <summary>
    /// Optional WebAuthn credential id hint persisted by the host. Seeds
    /// <see cref="AuthenticationModel.CredentialId"/> on first render so
    /// the platform credential picker can target a known credential.
    /// </summary>
    [Parameter]
    public string? CredentialId { get; set; }

    private readonly DialogOptions _dialogOptions = new()
    {
        CloseOnEscapeKey = true,
        CloseButton = true,
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
    };

    protected override async Task OnContextReadyAsync()
    {
        if (!string.IsNullOrEmpty(CredentialId))
        {
            Model.CredentialId = CredentialId;
        }
        await Model.CheckPrfSupport.ExecuteAsync();
    }

    /// <summary>
    /// Triggered when <see cref="AuthenticationModel.PendingCopy"/>
    /// changes. Runs the clipboard write + snackbar, then clears the
    /// signal so a subsequent click re-fires the trigger.
    /// </summary>
    protected override async Task OnPendingCopyChangedAsync(CancellationToken cancellationToken)
    {
        if (Model.PendingCopy is not { } payload)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", cancellationToken, payload);
            Snackbar.Add(Model.Localizer["Status_PublicKeyCopied"], Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add(Model.Localizer["Error_ClipboardCopy", ex.Message], Severity.Error);
        }
        finally
        {
            Model.PendingCopy = null;
        }
    }
}
