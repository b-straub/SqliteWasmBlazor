using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Shared;

/// <summary>
/// Backing model for <see cref="DatabaseErrorAlert"/>. Mirrors the
/// current boot <see cref="IDbInitFailure"/> from the singleton
/// <see cref="DbStateModel"/> via an auto-detected internal observer
/// — no event subscription, no <c>InvokeAsync</c>, no manual
/// <c>Subscriptions.Add</c>. Also owns the host-supplied
/// <see cref="RequestReset"/> recovery command.
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class DatabaseErrorAlertModel : ObservableModel
{
    public partial DatabaseErrorAlertModel(
        DbStateModel dbState,
        IHostDatabaseService service,
        StatusModel statusModel,
        IStringLocalizer<DatabaseErrorAlertModel> localizer);

    public partial IDbInitFailure? Failure { get; set; }

    /// <summary>
    /// True when the host registered a real <see cref="IHostDatabaseService"/>
    /// (not <see cref="NullHostDatabaseService"/>). The component hides
    /// the reset button when this is false.
    /// </summary>
    public bool CanReset => Service.IsAvailable;

    [ObservableCommand(nameof(RequestResetAsync), nameof(CanRequestReset), nameof(FormatResetError))]
    public partial IObservableCommandAsync RequestReset { get; }

    private bool CanRequestReset() => CanReset;

    /// <summary>
    /// Auto-detected internal observer (RxBlazorV2 §7) — keyed on
    /// <c>DbState.Failure</c>. Fires whenever <see cref="DbStateModel"/>'s
    /// failure payload changes; mirrors it onto the local
    /// <see cref="Failure"/> property so the bound razor re-renders the
    /// MudAlert. Also runs once at <c>OnContextReady</c> to seed the
    /// initial value.
    /// </summary>
    private void SyncFailure()
    {
        Failure = DbState.Failure;
    }

    protected override void OnContextReady()
    {
        // Seed the initial value — the auto-detected observer only fires
        // on subsequent changes.
        SyncFailure();
    }

    private async Task RequestResetAsync(CancellationToken cancellationToken)
    {
        await Service.ResetAsync(cancellationToken);
    }

    private string FormatResetError(Exception ex) =>
        Localizer["Error_Reset", ex.Message];
}
