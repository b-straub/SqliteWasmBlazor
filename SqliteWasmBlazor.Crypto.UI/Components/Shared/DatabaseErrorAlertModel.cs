using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Shared;

/// <summary>
/// Backing model for <see cref="DatabaseErrorAlert"/>. Holds the current
/// boot <see cref="Failure"/> and the host-supplied recovery command.
/// The component bridges the non-reactive <see cref="IDbInitializationStatus.Changed"/>
/// event into <see cref="Failure"/>; the model owns the
/// <see cref="RequestReset"/> command which delegates to
/// <see cref="IDatabaseResetService"/>.
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class DatabaseErrorAlertModel : ObservableModel
{
    public partial DatabaseErrorAlertModel(
        IDatabaseResetService resetService,
        StatusModel statusModel,
        IStringLocalizer<DatabaseErrorAlertModel> localizer);

    public partial IDbInitFailure? Failure { get; set; }

    /// <summary>
    /// True when the host registered a real <see cref="IDatabaseResetService"/>
    /// (not <see cref="NullDatabaseResetService"/>). The component hides
    /// the reset button when this is false.
    /// </summary>
    public bool CanReset => ResetService.IsAvailable;

    [ObservableCommand(nameof(RequestResetAsync), nameof(CanRequestReset), nameof(FormatResetError))]
    public partial IObservableCommandAsync RequestReset { get; }

    private bool CanRequestReset() => CanReset;

    private async Task RequestResetAsync(CancellationToken cancellationToken)
    {
        await ResetService.ResetAsync(cancellationToken);
    }

    private string FormatResetError(Exception ex) =>
        Localizer["Error_Reset", ex.Message];
}
