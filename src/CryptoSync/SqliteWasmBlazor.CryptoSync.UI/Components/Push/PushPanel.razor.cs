using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace SqliteWasmBlazor.CryptoSync.UI.Components.Push;

/// <summary>
/// Lightweight status panel for the host's <see cref="IPushNotifier"/>
/// wiring. Renders informationally: tells the operator whether a real
/// push impl is registered, or whether the default
/// <see cref="NullPushNotifier"/> is still in place.
///
/// <para>
/// <b>Why so thin.</b> Push payloads are byte-opaque — each domain
/// consumer (messenger, presence, etc.) defines its own shape and ships
/// its own compose UI. A "send message" dialog therefore lives in the
/// consumer, not in <c>CryptoSync.UI</c>. This base panel only surfaces
/// "is push wired or not" so a host has a place to render that state.
/// </para>
/// </summary>
public partial class PushPanel : ComponentBase
{
    [Inject]
    public required IPushNotifier PushNotifier { get; init; }

    [Inject]
    public required IStringLocalizer<PushPanel> Localizer { get; init; }
}
