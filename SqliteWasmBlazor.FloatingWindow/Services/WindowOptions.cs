namespace SqliteWasmBlazor.FloatingWindow.Services;

/// <summary>
/// Options for configuring floating window behavior.
/// </summary>
public sealed class WindowOptions
{
    /// <summary>
    /// Initial width of the window. Default: 400.
    /// </summary>
    public int Width { get; set; } = 400;

    /// <summary>
    /// Initial height of the window. Default: 300.
    /// </summary>
    public int Height { get; set; } = 300;

    /// <summary>
    /// Minimum width the window can be resized to. Default: 200.
    /// </summary>
    public int MinWidth { get; set; } = 200;

    /// <summary>
    /// Minimum height the window can be resized to. Default: 100.
    /// </summary>
    public int MinHeight { get; set; } = 100;

    /// <summary>
    /// Initial X position. Null for cascade positioning.
    /// </summary>
    public int? X { get; set; }

    /// <summary>
    /// Initial Y position. Null for cascade positioning.
    /// </summary>
    public int? Y { get; set; }

    /// <summary>
    /// Whether the window can be dragged. Default: true.
    /// </summary>
    public bool Draggable { get; set; } = true;

    /// <summary>
    /// Whether the window can be resized. Default: true.
    /// </summary>
    public bool Resizable { get; set; } = true;

    /// <summary>
    /// Whether to show the minimize button. Default: true.
    /// </summary>
    public bool CanMinimize { get; set; } = true;

    /// <summary>
    /// Whether to show the maximize button. Default: true.
    /// </summary>
    public bool CanMaximize { get; set; } = true;

    /// <summary>
    /// Whether to show the close button. Default: true.
    /// </summary>
    public bool CanClose { get; set; } = true;

    /// <summary>
    /// Whether to save and restore window state to localStorage. Default: false.
    /// </summary>
    public bool RememberState { get; set; }
}
