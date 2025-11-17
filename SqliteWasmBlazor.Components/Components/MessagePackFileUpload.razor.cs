using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using SqliteWasmBlazor.Components.Interop;
using System.Runtime.Versioning;
using MudBlazor;

namespace SqliteWasmBlazor.Components.Components;

[SupportedOSPlatform("browser")]
public partial class MessagePackFileUpload<T>
{
    [Inject]
    private ILogger<MessagePackFileUpload<T>> Logger { get; set; } = default!;
    /// <summary>
    /// Expected application identifier for validation (or null to skip app check)
    /// Set to prevent importing data from different applications
    /// </summary>
    [Parameter]
    public string? ExpectedAppIdentifier { get; set; }

    /// <summary>
    /// Expected schema hash (computed automatically from type T)
    /// Set to null to skip schema validation (not recommended)
    /// </summary>
    [Parameter]
    public string? ExpectedSchemaHash { get; set; }

    private async Task HandleFileSelectedAsync(InputFileChangeEventArgs e)
    {
        // Wrap entire handler to prevent exceptions from bubbling to Blazor error boundary
        try
        {
            await HandleFileSelectedInternalAsync(e);
        }
        catch
        {
            // Exception already handled in internal method - suppress Blazor error UI
        }
    }

    private async Task HandleFileSelectedInternalAsync(InputFileChangeEventArgs e)
    {
        Logger.LogDebug("Starting import of {Type}", typeof(T).Name);

        if (e.FileCount == 0)
        {
            return;
        }

        // Reset all progress values BEFORE showing overlay
        ProcessedRecords = 0;
        TotalRecords = 0;
        BytesRead = 0;
        TotalBytes = 0;
        CurrentBatch = 0;
        ProgressPercentage = 0;
        IsUploading = true;
        StateHasChanged();
        await Task.Yield(); // Let UI render clean state before starting

        try
        {
            await OnUploadStarted.InvokeAsync();

            var file = e.File;
            Logger.LogDebug("Importing file: {FileName}, Size: {Size}", file.Name, FormatBytes(file.Size));

            if (file.Size > MaxFileSize)
            {
                Snackbar.Add($"File size ({FormatBytes(file.Size)}) exceeds maximum allowed size ({FormatBytes(MaxFileSize)})", Severity.Error);
                return;
            }

            TotalBytes = file.Size;
            StateHasChanged();

            // Create progress reporter
            var streamingProgress = new Progress<(int current, int total)>(p =>
            {
                ProcessedRecords = p.current;
                TotalRecords = p.total == -1 ? ProcessedRecords : p.total;
                CurrentBatch = (ProcessedRecords / BatchSize) + 1;

                // Notify parent component and update UI
                InvokeAsync(async () =>
                {
                    await OnProgress.InvokeAsync((ProcessedRecords, TotalRecords));
                    StateHasChanged();
                    await Task.Yield(); // Yield to allow UI to update
                });
            });

            // Stream-deserialize directly from InputFile stream
            // This reads each entity as a separate MessagePack object
            // and invokes OnBulkInsertAsync for each batch
            // NEVER loads entire dataset into memory!
            await using var stream = file.OpenReadStream(MaxFileSize);

            // Wrap stream to track bytes read for progress
            var progressStream = new ProgressStream(stream, TotalBytes, bytesRead =>
            {
                BytesRead = bytesRead;
                ProgressPercentage = TotalBytes > 0 ? Math.Min(100, (int)((BytesRead * 100.0) / TotalBytes)) : 0;
            });

            var totalImported = await MessagePackSerializer<T>.DeserializeStreamAsync(
                progressStream,
                OnBulkInsertAsync,
                ExpectedSchemaHash,
                ExpectedAppIdentifier,
                Logger,
                BatchSize,
                streamingProgress);

            ProcessedRecords = totalImported;
            TotalRecords = totalImported;

            Logger.LogInformation("Import completed: {Count} records from {FileName}", totalImported, file.Name);

            // Show success snackbar
            Snackbar.Add($"Imported {ProcessedRecords:N0} records ({FormatBytes(file.Size)})", Severity.Success);

            await OnUploadCompleted.InvokeAsync(ProcessedRecords);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Import failed");
            Snackbar.Add($"Import failed: {ex.Message}", Severity.Error);
            await OnUploadFailed.InvokeAsync(ex.Message);
        }
        finally
        {
            IsUploading = false;
            StateHasChanged();
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
