namespace SqliteWasmBlazor.FloatingWindow.Services;

/// <summary>
/// Represents the snap zones for window snapping.
/// </summary>
public enum SnapZone
{
    None,
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>
/// Represents the current state of a floating window.
/// </summary>
public sealed class WindowState
{
    public required string Id { get; init; }
    public string? Title { get; set; }
    public string? Icon { get; set; }

    // Position
    public int X { get; set; }
    public int Y { get; set; }

    // Dimensions
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 300;
    public int MinWidth { get; set; } = 200;
    public int MinHeight { get; set; } = 100;

    // State
    public int ZIndex { get; set; }
    public bool IsMinimized { get; set; }
    public bool IsMaximized { get; set; }
    public bool IsFocused { get; set; }

    // Position before maximize (for restore)
    public int? PreMaximizeX { get; set; }
    public int? PreMaximizeY { get; set; }
    public int? PreMaximizeWidth { get; set; }
    public int? PreMaximizeHeight { get; set; }

    // Snap state
    public SnapZone SnapState { get; set; } = SnapZone.None;

    // Position before snap (for restore)
    public int? PreSnapX { get; set; }
    public int? PreSnapY { get; set; }
    public int? PreSnapWidth { get; set; }
    public int? PreSnapHeight { get; set; }

    /// <summary>
    /// Creates a copy of this state for localStorage persistence.
    /// </summary>
    public PersistedWindowState ToPersistedState() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        IsMaximized = IsMaximized,
        SnapState = SnapState,
        PreSnapX = PreSnapX,
        PreSnapY = PreSnapY,
        PreSnapWidth = PreSnapWidth,
        PreSnapHeight = PreSnapHeight
    };

    /// <summary>
    /// Applies persisted state from localStorage.
    /// </summary>
    public void ApplyPersistedState(PersistedWindowState persisted)
    {
        X = persisted.X;
        Y = persisted.Y;
        Width = persisted.Width;
        Height = persisted.Height;
        IsMaximized = persisted.IsMaximized;
        SnapState = persisted.SnapState;
        PreSnapX = persisted.PreSnapX;
        PreSnapY = persisted.PreSnapY;
        PreSnapWidth = persisted.PreSnapWidth;
        PreSnapHeight = persisted.PreSnapHeight;
    }
}

/// <summary>
/// Minimal state persisted to localStorage.
/// </summary>
public sealed class PersistedWindowState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }
    public SnapZone SnapState { get; set; }
    public int? PreSnapX { get; set; }
    public int? PreSnapY { get; set; }
    public int? PreSnapWidth { get; set; }
    public int? PreSnapHeight { get; set; }
}
