using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace SqliteWasmBlazor.WindowHelper;

public class FloatingDialogService : IAsyncDisposable
{
    private readonly IDialogService _dialogService;
    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _jsModule;

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

        await EnsureJsModuleAsync();

        // JS finds the newest unprocessed .mud-dialog via CSS selector
        // (same approach as MudBlazor.Extensions — no ID matching needed)
        await _jsModule!.InvokeVoidAsync("initFloatingDialog", new
        {
            draggable = options.Draggable,
            resizable = options.Resizable,
            windowId = options.RememberState ? options.WindowId : null,
            rememberState = options.RememberState && !string.IsNullOrEmpty(options.WindowId)
        });

        return dialogReference;
    }

    private async ValueTask EnsureJsModuleAsync()
    {
        _jsModule ??= await _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/SqliteWasmBlazor.WindowHelper/floating-dialog.js");
    }

    public async ValueTask DisposeAsync()
    {
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
