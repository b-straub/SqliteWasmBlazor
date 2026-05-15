using Microsoft.AspNetCore.Components;

namespace SqliteWasmBlazor.Crypto.UI.Components.Shared;

/// <summary>
/// Component-side glue for <see cref="DatabaseErrorAlertModel"/>. The
/// model auto-syncs <c>Failure</c> from
/// <see cref="Services.DbStateModel"/>; this partial just owns the
/// <see cref="ReloadPage"/> action (a synchronous browser navigation
/// that doesn't need to live in a command).
/// </summary>
public partial class DatabaseErrorAlert
{
    [Inject]
    public required NavigationManager Navigation { get; init; }

    private void ReloadPage() =>
        Navigation.NavigateTo(Navigation.BaseUri, forceLoad: true);
}
