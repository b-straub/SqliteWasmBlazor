namespace SqliteWasmBlazor.FloatingWindow.Services;

/// <summary>
/// Manages the state and z-ordering of all floating windows.
/// </summary>
public interface IWindowManager
{
    /// <summary>
    /// All registered windows.
    /// </summary>
    IReadOnlyDictionary<string, WindowState> Windows { get; }

    /// <summary>
    /// Fired when any window state changes (opened, closed, focused, moved, resized).
    /// </summary>
    event Action? OnWindowsChanged;

    /// <summary>
    /// Registers a new window and assigns initial z-index.
    /// </summary>
    void Register(string id, WindowState state);

    /// <summary>
    /// Unregisters a window.
    /// </summary>
    void Unregister(string id);

    /// <summary>
    /// Brings a window to the front (highest z-index).
    /// </summary>
    void BringToFront(string id);

    /// <summary>
    /// Gets the next cascade position for a new window.
    /// </summary>
    (int x, int y) GetCascadePosition();

    /// <summary>
    /// Notifies the manager that window state has changed.
    /// </summary>
    void NotifyStateChanged();

    /// <summary>
    /// Saves window state to localStorage.
    /// </summary>
    void SaveState(string id, PersistedWindowState state);

    /// <summary>
    /// Loads window state from localStorage (sync, returns cached value or null).
    /// </summary>
    PersistedWindowState? LoadState(string id);

    /// <summary>
    /// Loads window state from localStorage asynchronously.
    /// </summary>
    Task<PersistedWindowState?> LoadStateAsync(string id);
}
