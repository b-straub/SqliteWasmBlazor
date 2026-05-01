using Microsoft.AspNetCore.Components;

namespace SqliteWasmBlazor.Crypto.UI.Components.Shared;

/// <summary>
/// Component-side glue for <see cref="DatabaseErrorAlertModel"/>.
/// Bridges the non-reactive <see cref="IDbInitializationStatus.Changed"/>
/// event into the model's <see cref="DatabaseErrorAlertModel.Failure"/>
/// observable property, and owns the <see cref="ReloadPage"/> action
/// (a synchronous browser navigation that doesn't benefit from the
/// command pipeline).
/// </summary>
public partial class DatabaseErrorAlert : IDisposable
{
    [Inject]
    public required IDbInitializationStatus Status { get; init; }

    [Inject]
    public required NavigationManager Navigation { get; init; }

    protected override void OnContextReady()
    {
        Status.Changed += OnStatusChanged;
        RefreshFailure();
    }

    public void Dispose()
    {
        Status.Changed -= OnStatusChanged;
    }

    private void OnStatusChanged() => InvokeAsync(RefreshFailure);

    private void RefreshFailure() => Model.Failure = Status.Failure;

    private void ReloadPage() =>
        Navigation.NavigateTo(Navigation.BaseUri, forceLoad: true);
}
