using MessagePack;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using SqliteWasmBlazor.Components.Interop;
using System.Runtime.Versioning;
using MudBlazor;

namespace SqliteWasmBlazor.Components.Components;

[SupportedOSPlatform("browser")]
public partial class MessagePackFileDownload<T>
{
    [Inject]
    private ILogger<MessagePackFileDownload<T>> Logger { get; set; } = default!;

    /// <summary>
    /// Application identifier (optional - for validating imports)
    /// </summary>
    [Parameter]
    public string? AppIdentifier { get; set; }

    private async Task DownloadFileAsync()
    {
        // Wrap entire handler to prevent exceptions from bubbling to Blazor error boundary
        try
        {
            await DownloadFileInternalAsync();
        }
        catch
        {
            // Exception already handled in internal method - suppress Blazor error UI
        }
    }

    private async Task DownloadFileInternalAsync()
    {
        Logger.LogDebug("Starting export of {Type}", typeof(T).Name);

        // Reset all progress values BEFORE showing overlay
        ProcessedRecords = 0;
        CurrentPage = 0;
        TotalRecords = 0;
        TotalPages = 0;
        ProgressPercentage = 0;
        IsDownloading = true;
        StateHasChanged();
        await Task.Yield(); // Let UI render clean state before starting

        try
        {
            await OnDownloadStarted.InvokeAsync();

            // Get total record count
            TotalRecords = await GetTotalCountAsync();
            if (TotalRecords == 0)
            {
                Snackbar.Add("No records to export", Severity.Warning);
                return;
            }

            TotalPages = (int)Math.Ceiling(TotalRecords / (double)PageSize);
            Logger.LogInformation("Exporting {Count} {Type} records in {Pages} pages",
                TotalRecords, typeof(T).Name, TotalPages);

            StateHasChanged();

            using var memoryStream = new MemoryStream();

            // Write header once with total record count
            var header = MessagePackFileHeader.Create<T>(TotalRecords, AppIdentifier);
            Logger.LogDebug("Writing header: Type={Type}, SchemaHash={Hash}, Records={Count}",
                header.DataType, header.SchemaHash, header.RecordCount);
            await MessagePackSerializer.SerializeAsync(memoryStream, header);

            // Fetch and serialize each page (items only, no header per page)
            for (var pageIndex = 0; pageIndex < TotalPages; pageIndex++)
            {
                CurrentPage = pageIndex + 1;

                // Fetch page from data provider (SQL query with LIMIT/OFFSET)
                var pageItems = await GetPageAsync(pageIndex, PageSize);
                Logger.LogDebug("Fetched page {CurrentPage}/{TotalPages}: {Count} items",
                    CurrentPage, TotalPages, pageItems.Count);

                if (pageItems.Count == 0)
                {
                    Logger.LogDebug("Page {PageIndex} returned no items, stopping early", pageIndex);
                    break;
                }

                // Serialize items directly (no header)
                foreach (var item in pageItems)
                {
                    await MessagePackSerializer.SerializeAsync(memoryStream, item);
                }

                // Update progress after each page
                ProcessedRecords += pageItems.Count;
                ProgressPercentage = TotalRecords > 0 ? Math.Min(100, (ProcessedRecords * 100.0) / TotalRecords) : 0;

                await OnProgress.InvokeAsync((ProcessedRecords, TotalRecords));

                // Update UI with progress
                StateHasChanged();
                await Task.Yield(); // Yield to allow UI to update
            }

            Logger.LogDebug("Streaming complete: {Size}", FormatBytes(memoryStream.Length));

            // Convert MemoryStream to byte array for download
            var data = memoryStream.ToArray();

            // Download file using JSImport with ArraySegment (no-copy marshaling via MemoryView)
            FileOperationsInterop.DownloadMessagePackFile(new ArraySegment<byte>(data), FileName);

            Logger.LogInformation("Export completed: {Count} records, {Size}", ProcessedRecords, FormatBytes(data.Length));

            // Show success snackbar
            Snackbar.Add($"Exported {ProcessedRecords:N0} records ({FormatBytes(data.Length)}) to {FileName}", Severity.Success);

            await OnDownloadCompleted.InvokeAsync(ProcessedRecords);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Export failed");
            Snackbar.Add($"Export failed: {ex.Message}", Severity.Error);
            await OnDownloadFailed.InvokeAsync(ex.Message);
        }
        finally
        {
            IsDownloading = false;
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
