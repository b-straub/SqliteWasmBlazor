using Microsoft.AspNetCore.Components;
using SqliteWasmBlazor.CryptoSync.UI.Services;

namespace SqliteWasmBlazor.CryptoSync.UI.Components.Invitation;

/// <summary>
/// Composite invitation surface — admin-side. Until the host supplies an
/// <see cref="IAdminInvitationContext"/>, the panel renders an
/// informational placeholder. The downstream demo step provides the
/// WebAuthn-PRF backed context, at which point the create / ingest /
/// respond commands wire to the real <see cref="ContactInvitationService"/>
/// flow.
/// </summary>
public partial class InvitationPanel
{
    [Parameter]
    public IAdminInvitationContext? AdminContext { get; set; }
}
