using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Crypto.UI.Components.Encryption;

namespace SqliteWasmBlazor.Demo.Pages;

public partial class DatabaseEncryption
{
    /// <summary>
    /// Maximum size accepted from <see cref="MudFileUpload{T}"/> on disk
    /// import. The encrypted-VFS pool is bounded by the SAH capacity (~25
    /// slots × ~few MB each); 100 MiB covers practical envelopes with
    /// headroom while preventing a malicious / corrupt picker from
    /// pinning the WASM heap.
    /// </summary>
    private const long MaxImportEnvelopeBytes = 100L * 1024 * 1024;

    [Inject] public required IDialogService DialogService { get; init; }

    /// <summary>
    /// Triggered when <see cref="EncryptionModel.PendingDownload"/>
    /// changes. Runs the file-download interop and signals completion via
    /// the supplied <see cref="TaskCompletionSource"/> so the originating
    /// command can finish its <c>StatusModel</c> update.
    ///
    /// <para>
    /// JSInterop lives in the consumer page partial, never the model —
    /// RxBlazorV2 §5 (Component Triggers) is the canonical seam for "model
    /// emits a side-effect, host runs interop and acks completion".
    /// </para>
    /// </summary>
    protected override Task OnPendingDownloadChangedAsync(CancellationToken cancellationToken)
    {
        if (Model.PendingDownload is not { } payload)
        {
            return Task.CompletedTask;
        }

        try
        {
            FileOperationsInterop.DownloadMessagePackFile(
                new ArraySegment<byte>(payload.Bytes),
                payload.FileName);
            payload.Done.TrySetResult();
        }
        catch (Exception ex)
        {
            payload.Done.TrySetException(ex);
        }
        finally
        {
            Model.PendingDownload = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Confirmation gate for the destructive <c>Reset</c> command. Wired
    /// into <c>MudButtonAsyncRx.ConfirmExecutionAsync</c>; returns
    /// <c>true</c> when the user confirms (the button then runs the
    /// command), <c>false</c> on cancel (the command never executes).
    /// </summary>
    private Task<bool> ConfirmResetAsync()
        => ConfirmDestructiveAsync(
            title: Model.Localizer["Btn_Reset"],
            message: Model.Localizer["Confirm_Reset"],
            destructiveLabel: Model.Localizer["Btn_Reset"]);

    /// <summary>
    /// Shared confirm-or-cancel dialog for destructive operations. Cancel
    /// is the visually-primary default (filled, primary color); the
    /// destructive action is colored red and outlined to mark it as the
    /// consequential, non-default choice. Returns <c>true</c> only when
    /// the user explicitly clicks the destructive button.
    /// </summary>
    private async Task<bool> ConfirmDestructiveAsync(string title, string message, string destructiveLabel)
    {
        var parameters = new DialogParameters<Components.DestructiveConfirmDialog>
        {
            { x => x.Title, title },
            { x => x.Message, message },
            { x => x.DestructiveLabel, destructiveLabel },
            { x => x.CancelLabel, Model.Localizer["Btn_Cancel"].ToString() },
        };
        var dialog = await DialogService.ShowAsync<Components.DestructiveConfirmDialog>(title, parameters);
        var result = await dialog.Result;
        return result is { Canceled: false, Data: true };
    }

    /// <summary>
    /// Unified file-pick handler for the import flow. Sniffs the picked
    /// file's extension and dispatches to the envelope (.eds → guided
    /// passkey-rebinding import) or plain-ZIP (.zip → state-aware
    /// dispatch via <see cref="EncryptionModel.ImportAllDatabases"/>)
    /// path. One picker + one handler keeps the page surface compact;
    /// the model commands' <c>CanExecute</c> + the encrypted service's
    /// state-aware dispatch handle the rest.
    /// </summary>
    private async Task HandleImportPickedAsync(IBrowserFile? file)
    {
        if (file is null) return;
        var bytes = await ReadPickedAsync(file);
        if (bytes is null) return;

        if (file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await HandleZipBytesAsync(bytes);
        }
        else if (file.Name.EndsWith(".eds", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEnvelopeBytesAsync(bytes);
        }
    }

    private async Task HandleEnvelopeBytesAsync(byte[] bytes)
    {
        var confirmed = await ConfirmDestructiveAsync(
            title: Model.Localizer["Btn_ImportDisk"],
            message: Model.Localizer["Confirm_ImportDisk"],
            destructiveLabel: Model.Localizer["Btn_ImportDisk"]);

        if (confirmed)
        {
            await Model.ImportDisk.ExecuteAsync(bytes);
        }
    }

    private async Task HandleZipBytesAsync(byte[] bytes)
    {
        // State-aware warning: a plain ZIP on a Locked disk breaks encryption,
        // on an Unlocked disk preserves it, on a Plain disk just replaces.
        // Session.ImportAllDatabasesAsync owns the dispatch; the page only
        // owns the right confirmation prompt for each outcome.
        var messageKey = Model switch
        {
            { IsLocked: true } => "Confirm_ImportAllDatabases_Locked",
            { IsUnlocked: true } => "Confirm_ImportAllDatabases_Unlocked",
            _ => "Confirm_ImportAllDatabases",
        };
        var confirmed = await ConfirmDestructiveAsync(
            title: Model.Localizer["Btn_ImportAllDatabases"],
            message: Model.Localizer[messageKey],
            destructiveLabel: Model.Localizer["Btn_ImportAllDatabases"]);

        if (confirmed)
        {
            await Model.ImportAllDatabases.ExecuteAsync(bytes);
        }
    }

    /// <summary>
    /// Common file-bytes read with size cap. Returns null on
    /// no-file-picked; throws on oversize.
    /// </summary>
    private static async Task<byte[]?> ReadPickedAsync(IBrowserFile? file)
    {
        if (file is null) return null;
        await using var stream = file.OpenReadStream(maxAllowedSize: MaxImportEnvelopeBytes);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
