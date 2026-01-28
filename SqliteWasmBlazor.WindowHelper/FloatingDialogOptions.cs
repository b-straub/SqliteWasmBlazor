using MudBlazor;

namespace SqliteWasmBlazor.WindowHelper;

public class FloatingDialogOptions
{
    /// <summary>
    /// Unique identifier for this window. Required when RememberState is true.
    /// Used as key for localStorage persistence.
    /// </summary>
    public string? WindowId { get; set; }

    /// <summary>
    /// When true, window position and size are saved to localStorage
    /// and restored when the window is opened again.
    /// Requires WindowId to be set.
    /// </summary>
    public bool RememberState { get; set; }

    public bool Draggable { get; set; } = true;
    public bool Resizable { get; set; } = true;
    public bool CloseButton { get; set; } = true;
    public bool CloseOnEscapeKey { get; set; } = true;
    public MaxWidth MaxWidth { get; set; } = MaxWidth.Medium;
    public bool FullWidth { get; set; } = true;
    public DialogPosition Position { get; set; } = DialogPosition.Center;
}
