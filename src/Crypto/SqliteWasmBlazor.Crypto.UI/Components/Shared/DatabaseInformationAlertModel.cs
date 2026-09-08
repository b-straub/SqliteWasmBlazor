using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Abstractions;

namespace SqliteWasmBlazor.Crypto.UI.Components.Shared;

/// <summary>
/// Backing model for <see cref="DatabaseInformationAlert"/>. Mirrors the
/// current boot <see cref="IDbInitFailure"/> and
/// <see cref="DbInitState"/> from the singleton
/// <see cref="DbStateModel"/> via an auto-detected internal observer
/// — no event subscription, no <c>InvokeAsync</c>, no manual
/// <c>Subscriptions.Add</c>. Also owns the host-supplied
/// <see cref="RequestReset"/> recovery command.
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class DatabaseInformationAlertModel : ObservableModel
{
    public partial DatabaseInformationAlertModel(
        DbStateModel dbState,
        IHostRecoveryService service,
        StatusModel statusModel,
        IStringLocalizer<DatabaseInformationAlertModel> localizer);

    public partial IDbInitFailure? Failure { get; set; }

    /// <summary>
    /// The current boot/lifecycle state. Mirrored alongside
    /// <see cref="Failure"/> because the states worth showing are not all
    /// failures: a migration is progress, not a fault.
    /// </summary>
    public partial DbInitState State { get; set; } = DbInitState.NOT_STARTED;

    /// <summary>
    /// True while the database is doing work the user has to wait through.
    /// Applying a migration over a large encrypted database takes seconds
    /// with nothing else on screen to explain it, so it gets a progress
    /// notice rather than silence.
    /// </summary>
    public bool IsBusy => State is DbInitState.INITIALIZING or DbInitState.MIGRATING;

    /// <summary>Wait text for the current <see cref="IsBusy"/> state.</summary>
    public string BusyMessage => State switch
    {
        DbInitState.MIGRATING => Localizer["Busy_Migrating"],
        _ => Localizer["Busy_Initializing"]
    };

    /// <summary>
    /// True when the host registered a real <see cref="IHostRecoveryService"/>
    /// (not <see cref="NullHostRecoveryService"/>). The component hides
    /// the reset button when this is false.
    /// </summary>
    public bool CanReset => Service.IsAvailable;

    [ObservableCommand(nameof(RequestResetAsync), nameof(CanRequestReset), nameof(FormatResetError))]
    public partial IObservableCommandAsync RequestReset { get; }

    private bool CanRequestReset() => CanReset;

    /// <summary>
    /// Auto-detected internal observer (RxBlazorV2 §7) — keyed on
    /// <c>DbState.State</c> and <c>DbState.Failure</c>. Fires whenever
    /// either changes; mirrors both onto the local properties so the bound
    /// razor re-renders the MudAlert. Also runs once at
    /// <c>OnContextReady</c> to seed the initial values.
    /// </summary>
    private void SyncDbState()
    {
        State = DbState.State;
        Failure = DbState.Failure;
    }

    protected override void OnContextReady()
    {
        // Seed the initial values — the auto-detected observer only fires
        // on subsequent changes.
        SyncDbState();
    }

    private async Task RequestResetAsync(CancellationToken cancellationToken)
    {
        await Service.ResetAsync(cancellationToken);
    }

    private string FormatResetError(Exception ex) =>
        Localizer["Error_Reset", ex.Message];
}
