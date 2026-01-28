using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace SqliteWasmBlazor.WindowHelper;

public class FloatingDialogService : IAsyncDisposable
{
    private readonly IDialogService _dialogService;
    private readonly IJSRuntime _jsRuntime;
    private readonly Dictionary<string, IDialogReference> _openDialogs = [];
    private IJSObjectReference? _jsModule;

    /// <summary>
    /// Fired when any floating dialog is closed. Parameter is the WindowId.
    /// </summary>
    public event Action<string>? OnDialogClosed;

    public FloatingDialogService(IDialogService dialogService, IJSRuntime jsRuntime)
    {
        _dialogService = dialogService;
        _jsRuntime = jsRuntime;
    }

    public async Task<IDialogReference> ShowAsync<T>(
        string title,
        FloatingDialogOptions? options = null,
        DialogParameters? parameters = null) where T : ComponentBase
    {
        options ??= new FloatingDialogOptions();

        var dialogOptions = new DialogOptions
        {
            BackdropClick = false,
            NoHeader = false,
            CloseButton = options.CloseButton,
            CloseOnEscapeKey = options.CloseOnEscapeKey,
            MaxWidth = options.MaxWidth,
            FullWidth = options.FullWidth,
            Position = options.Position
        };

        var dialogReference = await _dialogService.ShowAsync<T>(title, parameters ?? new DialogParameters(), dialogOptions);

        var windowId = options.WindowId ?? Guid.NewGuid().ToString();
        _openDialogs[windowId] = dialogReference;

        // Track when dialog closes to fire event and cleanup
        TrackDialogClosure(windowId, dialogReference);

        var jsModule = await EnsureJsModuleAsync();

        await jsModule.InvokeVoidAsync("initFloatingDialog", new
        {
            title,
            draggable = options.Draggable,
            resizable = options.Resizable,
            windowId = options.RememberState ? options.WindowId : null,
            rememberState = options.RememberState && !string.IsNullOrEmpty(options.WindowId)
        });

        return dialogReference;
    }

    private async void TrackDialogClosure(string windowId, IDialogReference dialogReference)
    {
        try
        {
            await dialogReference.Result;
        }
        catch
        {
            // Dialog may have been disposed
        }
        finally
        {
            _openDialogs.Remove(windowId);
            OnDialogClosed?.Invoke(windowId);
        }
    }

    /// <summary>
    /// Checks if a dialog with the specified WindowId is currently open.
    /// </summary>
    public bool IsOpen(string? windowId)
    {
        if (windowId is null || !_openDialogs.TryGetValue(windowId, out var dialogRef))
        {
            return false;
        }

        // Check if dialog is still open (Result task not completed)
        if (dialogRef.Result.IsCompleted)
        {
            _openDialogs.Remove(windowId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Closes all open floating dialogs. Call when navigating away
    /// from a page that uses floating dialogs to prevent focus errors.
    /// </summary>
    public void CloseAll()
    {
        foreach (var dialog in _openDialogs.Values)
        {
            dialog.Close();
        }
        _openDialogs.Clear();
    }

    private async ValueTask<IJSObjectReference> EnsureJsModuleAsync()
    {
        _jsModule ??= await _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/SqliteWasmBlazor.WindowHelper/floating-dialog.js");

        if (_jsModule is null)
        {
            throw new InvalidOperationException("Failed to load floating-dialog.js module");
        }

        return _jsModule;
    }

    public async ValueTask DisposeAsync()
    {
        CloseAll();

        if (_jsModule is not null)
        {
            try
            {
                await _jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected, safe to ignore
            }
        }
    }
}
