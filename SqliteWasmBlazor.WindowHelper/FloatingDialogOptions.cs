using MudBlazor;

namespace SqliteWasmBlazor.WindowHelper;

public class FloatingDialogOptions
{
    public bool Draggable { get; set; } = true;
    public bool Resizable { get; set; } = true;
    public bool CloseButton { get; set; } = true;
    public bool CloseOnEscapeKey { get; set; } = true;
    public MaxWidth MaxWidth { get; set; } = MaxWidth.Medium;
    public bool FullWidth { get; set; } = true;
    public DialogPosition Position { get; set; } = DialogPosition.Center;
}
