using System.Text.Json;
using Microsoft.JSInterop;

namespace SqliteWasmBlazor.FloatingWindow.Services;

/// <summary>
/// Manages the state and z-ordering of all floating windows.
/// </summary>
public sealed class WindowManager : IWindowManager, IAsyncDisposable
{
    // MudBlazor z-index values: appbar=1300, drawer=1200, dialog=1400, popover=1500
    // Start above appbar/drawer so floating windows aren't hidden behind nav
    private const int BaseZIndex = 1400;
    private const int CascadeOffsetX = 30;
    private const int CascadeOffsetY = 30;
    private const int MaxCascadeSteps = 10;
    private const string StoragePrefix = "fw-state:";

    private readonly IJSRuntime _jsRuntime;
    private readonly Dictionary<string, WindowState> _windows = [];
    private readonly Dictionary<string, PersistedWindowState> _persistedStateCache = [];
    private int _zIndexCounter = BaseZIndex;
    private int _cascadeStep;

    public WindowManager(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public IReadOnlyDictionary<string, WindowState> Windows => _windows;

    public event Action? OnWindowsChanged;

    public void Register(string id, WindowState state)
    {
        state.ZIndex = ++_zIndexCounter;
        _windows[id] = state;
        OnWindowsChanged?.Invoke();
    }

    public void Unregister(string id)
    {
        if (_windows.Remove(id))
        {
            OnWindowsChanged?.Invoke();
        }
    }

    public void BringToFront(string id)
    {
        if (!_windows.TryGetValue(id, out var state))
        {
            return;
        }

        // Unfocus all other windows
        foreach (var (windowId, windowState) in _windows)
        {
            windowState.IsFocused = windowId == id;
        }

        state.ZIndex = ++_zIndexCounter;
        OnWindowsChanged?.Invoke();
    }

    public (int x, int y) GetCascadePosition()
    {
        // Start below typical MudBlazor appbar (64px) with some margin
        var x = 50 + (_cascadeStep * CascadeOffsetX);
        var y = 80 + (_cascadeStep * CascadeOffsetY);

        _cascadeStep = (_cascadeStep + 1) % MaxCascadeSteps;

        return (x, y);
    }

    public void NotifyStateChanged()
    {
        OnWindowsChanged?.Invoke();
    }

    public void SaveState(string id, PersistedWindowState state)
    {
        _persistedStateCache[id] = state;

        // Fire and forget - we don't want to block on localStorage
        _ = SaveToStorageAsync(id, state);
    }

    public PersistedWindowState? LoadState(string id)
    {
        // Return cached value if available
        if (_persistedStateCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        return null;
    }

    public async Task<PersistedWindowState?> LoadStateAsync(string id)
    {
        // First check cache
        if (_persistedStateCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        // Load from localStorage
        try
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", StoragePrefix + id);
            if (!string.IsNullOrEmpty(json))
            {
                var state = JsonSerializer.Deserialize<PersistedWindowState>(json);
                if (state is not null)
                {
                    _persistedStateCache[id] = state;
                    return state;
                }
            }
        }
        catch
        {
            // Ignore localStorage errors
        }

        return null;
    }

    private async Task SaveToStorageAsync(string id, PersistedWindowState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", StoragePrefix + id, json);
        }
        catch
        {
            // localStorage might be disabled or full
        }
    }

    public async ValueTask DisposeAsync()
    {
        _windows.Clear();
        _persistedStateCache.Clear();
        await ValueTask.CompletedTask;
    }
}
