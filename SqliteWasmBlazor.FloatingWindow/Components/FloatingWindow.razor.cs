using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SqliteWasmBlazor.FloatingWindow.Services;

namespace SqliteWasmBlazor.FloatingWindow.Components;

public partial class FloatingWindow : IAsyncDisposable
{
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<FloatingWindow>? _dotNetRef;
    private WindowState? _state;
    private bool _isInitialized;
    private bool _jsInteropInitialized;
    private bool _needsResizeReinit;

    #region Parameters

    /// <summary>
    /// Unique identifier for this window. Required for state persistence.
    /// </summary>
    [Parameter, EditorRequired]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Window title displayed in the header.
    /// </summary>
    [Parameter]
    public string? Title { get; set; }

    /// <summary>
    /// MudBlazor icon displayed before the title.
    /// </summary>
    [Parameter]
    public string? Icon { get; set; }

    /// <summary>
    /// Content rendered inside the window body.
    /// </summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Initial width of the window.
    /// </summary>
    [Parameter]
    public int Width { get; set; } = 400;

    /// <summary>
    /// Initial height of the window.
    /// </summary>
    [Parameter]
    public int Height { get; set; } = 300;

    /// <summary>
    /// Minimum width the window can be resized to.
    /// </summary>
    [Parameter]
    public int MinWidth { get; set; } = 200;

    /// <summary>
    /// Minimum height the window can be resized to.
    /// </summary>
    [Parameter]
    public int MinHeight { get; set; } = 100;

    /// <summary>
    /// Initial X position. Null for cascade positioning.
    /// </summary>
    [Parameter]
    public int? X { get; set; }

    /// <summary>
    /// Initial Y position. Null for cascade positioning.
    /// </summary>
    [Parameter]
    public int? Y { get; set; }

    /// <summary>
    /// Whether the window is open/visible.
    /// </summary>
    [Parameter]
    public bool IsOpen { get; set; }

    /// <summary>
    /// Callback when IsOpen changes.
    /// </summary>
    [Parameter]
    public EventCallback<bool> IsOpenChanged { get; set; }

    /// <summary>
    /// Whether the window can be dragged.
    /// </summary>
    [Parameter]
    public bool Draggable { get; set; } = true;

    /// <summary>
    /// Whether the window can be resized.
    /// </summary>
    [Parameter]
    public bool Resizable { get; set; } = true;

    /// <summary>
    /// Whether to show the minimize button.
    /// </summary>
    [Parameter]
    public bool CanMinimize { get; set; } = true;

    /// <summary>
    /// Whether to show the maximize button.
    /// </summary>
    [Parameter]
    public bool CanMaximize { get; set; } = true;

    /// <summary>
    /// Whether to show the close button.
    /// </summary>
    [Parameter]
    public bool CanClose { get; set; } = true;

    /// <summary>
    /// Whether to save/restore window state to localStorage.
    /// </summary>
    [Parameter]
    public bool RememberState { get; set; }

    /// <summary>
    /// Whether the window can be snapped to edges.
    /// </summary>
    [Parameter]
    public bool CanSnap { get; set; } = true;

    /// <summary>
    /// Callback when the window is closed.
    /// </summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>
    /// Callback when the window gains focus.
    /// </summary>
    [Parameter]
    public EventCallback OnFocus { get; set; }

    /// <summary>
    /// Callback when the window is minimized.
    /// </summary>
    [Parameter]
    public EventCallback OnMinimize { get; set; }

    /// <summary>
    /// Callback when the window is maximized.
    /// </summary>
    [Parameter]
    public EventCallback OnMaximize { get; set; }

    /// <summary>
    /// Callback when the window is restored from maximized state.
    /// </summary>
    [Parameter]
    public EventCallback OnRestore { get; set; }

    #endregion

    protected override void OnInitialized()
    {
        WindowManager.OnWindowsChanged += OnWindowsChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (IsOpen && !_isInitialized)
        {
            await InitializeWindowAsync();
        }
        else if (!IsOpen && _isInitialized)
        {
            await CleanupWindowAsync();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (IsOpen && _isInitialized && !_jsInteropInitialized)
        {
            await InitializeJsInteropAsync();
        }
        else if (_needsResizeReinit && _jsModule is not null)
        {
            _needsResizeReinit = false;
            // Reinitialize resize handlers after restore from maximized
            // (the resize handles were removed from DOM when maximized)
            if (Resizable)
            {
                await _jsModule.InvokeVoidAsync("initResize", $"fw-{Id}", _dotNetRef);
            }
        }
    }

    private async Task InitializeWindowAsync()
    {
        _isInitialized = true;

        // Try to load persisted state (await to prevent position jump on first load)
        var persistedState = RememberState ? await WindowManager.LoadStateAsync(Id) : null;

        // Determine initial position
        int x, y;
        if (persistedState is not null)
        {
            x = persistedState.X;
            y = persistedState.Y;
        }
        else if (X.HasValue && Y.HasValue)
        {
            x = X.Value;
            y = Y.Value;
        }
        else
        {
            (x, y) = WindowManager.GetCascadePosition();
        }

        _state = new WindowState
        {
            Id = Id,
            Title = Title,
            Icon = Icon,
            X = x,
            Y = y,
            Width = persistedState?.Width ?? Width,
            Height = persistedState?.Height ?? Height,
            MinWidth = MinWidth,
            MinHeight = MinHeight,
            IsMaximized = persistedState?.IsMaximized ?? false,
            IsFocused = true
        };

        WindowManager.Register(Id, _state);
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    private async Task InitializeJsInteropAsync()
    {
        try
        {
            _jsModule = await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/SqliteWasmBlazor.FloatingWindow/floating-window.js");

            if (Draggable)
            {
                var isSnapped = _state is not null && _state.SnapState != Services.SnapZone.None;
                await _jsModule.InvokeVoidAsync("initDrag", $"fw-{Id}", _dotNetRef, CanSnap, isSnapped, _state?.PreSnapWidth, _state?.PreSnapHeight);
            }

            if (Resizable)
            {
                await _jsModule.InvokeVoidAsync("initResize", $"fw-{Id}", _dotNetRef);
            }

            _jsInteropInitialized = true;
        }
        catch (JSException)
        {
            // JS interop might fail during prerendering
        }
    }

    private async Task CleanupWindowAsync()
    {
        _isInitialized = false;
        _jsInteropInitialized = false;

        if (_jsModule is not null)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("dispose", $"fw-{Id}");
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
            }
        }

        WindowManager.Unregister(Id);
        _dotNetRef?.Dispose();
        _dotNetRef = null;
        _state = null;
    }

    private void OnWindowsChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private void BringToFront()
    {
        WindowManager.BringToFront(Id);
        OnFocus.InvokeAsync();
    }

    private async Task Minimize()
    {
        if (_state is null)
        {
            return;
        }

        _state.IsMinimized = true;
        WindowManager.NotifyStateChanged();
        await OnMinimize.InvokeAsync();
    }

    private async Task ToggleMaximizeOrUnsnap()
    {
        if (_state is null)
        {
            return;
        }

        // If snapped, unsnap first
        if (_state.SnapState != Services.SnapZone.None)
        {
            await UnsnapWindow();
            return;
        }

        // Otherwise toggle maximize
        await ToggleMaximize();
    }

    private async Task ToggleMaximize()
    {
        if (_state is null)
        {
            return;
        }

        if (_state.IsMaximized)
        {
            // Restore
            _state.IsMaximized = false;
            _needsResizeReinit = true; // Resize handles will be re-added to DOM
            if (_state.PreMaximizeX.HasValue)
            {
                _state.X = _state.PreMaximizeX.Value;
                _state.Y = _state.PreMaximizeY!.Value;
                _state.Width = _state.PreMaximizeWidth!.Value;
                _state.Height = _state.PreMaximizeHeight!.Value;
            }
            await OnRestore.InvokeAsync();
        }
        else
        {
            // Maximize
            _state.PreMaximizeX = _state.X;
            _state.PreMaximizeY = _state.Y;
            _state.PreMaximizeWidth = _state.Width;
            _state.PreMaximizeHeight = _state.Height;
            _state.IsMaximized = true;
            await OnMaximize.InvokeAsync();
        }

        SaveStateIfNeeded();
        WindowManager.NotifyStateChanged();
    }

    private async Task UnsnapWindow()
    {
        if (_state is null)
        {
            return;
        }

        // Restore pre-snap geometry
        if (_state.PreSnapX.HasValue)
        {
            _state.X = _state.PreSnapX.Value;
            _state.Y = _state.PreSnapY!.Value;
            _state.Width = _state.PreSnapWidth!.Value;
            _state.Height = _state.PreSnapHeight!.Value;
        }

        // Clear snap state
        _state.SnapState = Services.SnapZone.None;
        _state.PreSnapX = null;
        _state.PreSnapY = null;
        _state.PreSnapWidth = null;
        _state.PreSnapHeight = null;

        // Resize handles will be re-added to DOM
        _needsResizeReinit = true;

        // Update JS snap state
        if (_jsModule is not null)
        {
            await _jsModule.InvokeVoidAsync("updateSnapState", $"fw-{Id}", CanSnap, false, null, null);
        }

        SaveStateIfNeeded();
        await OnRestore.InvokeAsync();
        WindowManager.NotifyStateChanged();
    }

    private async Task Close()
    {
        IsOpen = false;
        await IsOpenChanged.InvokeAsync(false);
        await OnClose.InvokeAsync();
        await CleanupWindowAsync();
    }

    /// <summary>
    /// Restores a minimized window.
    /// </summary>
    public void Restore()
    {
        if (_state is null)
        {
            return;
        }

        _state.IsMinimized = false;
        WindowManager.BringToFront(Id);
    }

    #region JS Interop Callbacks

    [JSInvokable]
    public void OnDragEnd(double x, double y)
    {
        if (_state is null)
        {
            return;
        }

        // Sync final position from JS to Blazor state
        _state.X = (int)x;
        _state.Y = (int)y;

        SaveStateIfNeeded();
        StateHasChanged();
    }

    [JSInvokable]
    public void OnResizeEnd(double x, double y, double width, double height)
    {
        if (_state is null)
        {
            return;
        }

        // Sync final geometry from JS to Blazor state
        _state.X = (int)x;
        _state.Y = (int)y;
        _state.Width = (int)width;
        _state.Height = (int)height;

        SaveStateIfNeeded();
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnSnapToEdge(string zone)
    {
        if (_state is null)
        {
            return;
        }

        // Parse snap zone
        var snapZone = zone switch
        {
            "left" => Services.SnapZone.Left,
            "right" => Services.SnapZone.Right,
            "top" => Services.SnapZone.Top,
            "bottom" => Services.SnapZone.Bottom,
            _ => Services.SnapZone.None
        };

        if (snapZone == Services.SnapZone.None)
        {
            return;
        }

        // Store pre-snap geometry (only if not already snapped)
        if (_state.SnapState == Services.SnapZone.None)
        {
            _state.PreSnapX = _state.X;
            _state.PreSnapY = _state.Y;
            _state.PreSnapWidth = _state.Width;
            _state.PreSnapHeight = _state.Height;
        }

        // Set snap state - CSS classes will handle the actual positioning
        _state.SnapState = snapZone;

        // Update JS snap state
        if (_jsModule is not null)
        {
            await _jsModule.InvokeVoidAsync("updateSnapState", $"fw-{Id}", CanSnap, true, _state.PreSnapWidth, _state.PreSnapHeight);
        }

        SaveStateIfNeeded();
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnRestoreFromSnap()
    {
        if (_state is null)
        {
            return;
        }

        // Restore pre-snap dimensions (position is handled by drag)
        if (_state.PreSnapWidth.HasValue && _state.PreSnapHeight.HasValue)
        {
            _state.Width = _state.PreSnapWidth.Value;
            _state.Height = _state.PreSnapHeight.Value;
        }

        // Clear snap state
        _state.SnapState = Services.SnapZone.None;
        _state.PreSnapX = null;
        _state.PreSnapY = null;
        _state.PreSnapWidth = null;
        _state.PreSnapHeight = null;

        // Resize handles will be re-added to DOM
        _needsResizeReinit = true;

        // Update JS snap state
        if (_jsModule is not null)
        {
            await _jsModule.InvokeVoidAsync("updateSnapState", $"fw-{Id}", CanSnap, false, null, null);
        }

        SaveStateIfNeeded();
        StateHasChanged();
    }

    #endregion

    private void SaveStateIfNeeded()
    {
        if (RememberState && _state is not null)
        {
            WindowManager.SaveState(Id, _state.ToPersistedState());
        }
    }

    private string GetSnapClass()
    {
        if (_state is null)
        {
            return string.Empty;
        }

        return _state.SnapState switch
        {
            Services.SnapZone.Left => "fw-snapped-left",
            Services.SnapZone.Right => "fw-snapped-right",
            Services.SnapZone.Top => "fw-snapped-top",
            Services.SnapZone.Bottom => "fw-snapped-bottom",
            _ => string.Empty
        };
    }

    public async ValueTask DisposeAsync()
    {
        WindowManager.OnWindowsChanged -= OnWindowsChanged;

        if (_jsModule is not null)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("dispose", $"fw-{Id}");
                await _jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
            }
        }

        if (_isInitialized)
        {
            WindowManager.Unregister(Id);
        }

        _dotNetRef?.Dispose();
    }
}
