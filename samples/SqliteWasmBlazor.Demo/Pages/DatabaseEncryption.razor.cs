using SqliteWasmBlazor.Components.Interop;

namespace SqliteWasmBlazor.Demo.Pages;

public partial class DatabaseEncryption
{
    protected override async Task OnContextReadyAsync()
    {
        // Probe DB existence + magic-header state on every page entry so the
        // state pill is correct before the user clicks anything.
        await Model.Refresh.ExecuteAsync();
    }

    /// <summary>
    /// Triggered when <see cref="DatabaseEncryptionModel.PendingDownload"/>
    /// changes. Runs the file-download interop and signals completion via
    /// the supplied <c>TaskCompletionSource</c> so the originating command
    /// can finish its <c>StatusModel</c> update.
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
}
