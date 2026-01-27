namespace SqliteWasmBlazor.Demo.Services;

/// <summary>
/// Lightweight notification service that broadcasts data change events
/// so all open multi-view windows can refresh when data is modified in any view.
/// </summary>
public sealed class TodoDataNotifier
{
    public event Action? OnDataChanged;

    public void NotifyDataChanged()
    {
        OnDataChanged?.Invoke();
    }
}
