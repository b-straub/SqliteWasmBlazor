using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace SqliteWasmBlazor.CryptoSync.UI.Components.Contacts;

/// <summary>
/// Component-side glue for <see cref="ContactsModel"/>. Bridges the
/// reactive <see cref="ContactsModel.PendingCopy"/> signal into the
/// browser clipboard + MudBlazor snackbar — those are inherently
/// component-layer concerns and don't belong on the model.
/// </summary>
public partial class ContactsPanel
{
    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ISnackbar Snackbar { get; init; }

    protected override async Task OnContextReadyAsync()
    {
        await Model.LoadContacts.ExecuteAsync();
    }

    /// <summary>
    /// Triggered when <see cref="ContactsModel.PendingCopy"/> changes.
    /// Performs the JS clipboard write + snackbar, then clears the signal
    /// so the next click re-fires the trigger.
    /// </summary>
    protected override async Task OnPendingCopyChangedAsync(CancellationToken cancellationToken)
    {
        if (Model.PendingCopy is not { } request)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", cancellationToken, request.PublicKey);
            Snackbar.Add(Model.Localizer["Status_CopiedFor", request.Label], Severity.Success);
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
