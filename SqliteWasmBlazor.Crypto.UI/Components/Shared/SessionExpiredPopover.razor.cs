using Microsoft.AspNetCore.Components;

namespace SqliteWasmBlazor.Crypto.UI.Components.Shared;

/// <summary>
/// Component-side glue for <see cref="SessionExpiredPopoverModel"/>.
/// Holds optional override-parameters for the body text + button labels;
/// when not supplied, the panel falls back to the localized defaults from
/// <c>SessionExpiredPopoverModel.resx</c>.
/// </summary>
public partial class SessionExpiredPopover
{
    /// <summary>Override the body text. Falls back to the localized <c>Default_Message</c>.</summary>
    [Parameter]
    public string? Message { get; set; }

    /// <summary>Override the re-authenticate button label. Falls back to the localized <c>Default_ReAuthLabel</c>.</summary>
    [Parameter]
    public string? ReAuthenticateLabel { get; set; }

    /// <summary>Override the dismiss button label. Falls back to the localized <c>Default_DismissLabel</c>.</summary>
    [Parameter]
    public string? DismissLabel { get; set; }
}
