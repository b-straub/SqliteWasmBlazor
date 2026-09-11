using Microsoft.Extensions.Localization;
using RxBlazorV2.MudBlazor.Components;

namespace SqliteWasmBlazor.Demo.Services;

/// <summary>
/// Turns initialization states into snackbar text.
/// </summary>
/// <remarks>
/// <para>
/// The library reports what happened and this decides what to say about it. That
/// split is why <see cref="IDbInitNotifier"/> carries no strings: the base
/// package has no localization and no UI framework, so wording there would ship
/// English into every app.
/// </para>
/// <para>
/// Only the states worth a one-off remark are handled. Current-state rendering
/// — the locked notice, the progress bar — belongs to
/// <c>&lt;DatabaseInformationAlert/&gt;</c>, which observes the same states
/// continuously. Repeating them here would say everything twice.
/// </para>
/// <para>
/// <b>Why MIGRATING is queued rather than added.</b> Every migration counts as
/// pending on a database that has had none applied, so creating one reports
/// MIGRATING for a few milliseconds — announcing that is how a state people
/// should trust becomes one they learn to ignore. <c>QueueInfo</c> holds the
/// message for <c>StatusModel.QueueWindow</c> (1s) and the
/// <see cref="DbInitState.READY"/> branch drops it, so a migration too quick to
/// matter is never shown while a real one still is. The failure branches need no
/// such pairing: <c>AddError</c> cancels anything queued on its way past.
/// </para>
/// </remarks>
public sealed class DemoDbInitNotifier(
    StatusModel statusModel,
    IStringLocalizer<DemoDbInitNotifier> localizer) : IDbInitNotifier
{
    /// <inheritdoc />
    public ValueTask NotifyAsync(
        DbInitNotification notification,
        CancellationToken cancellationToken = default)
    {
        switch (notification.State)
        {
            case DbInitState.MIGRATING:
                statusModel.QueueInfo(
                    localizer["Status_Migrating", notification.DatabaseName ?? string.Empty],
                    nameof(DemoDbInitNotifier));
                break;

            case DbInitState.TAB_LOCKED:
            case DbInitState.TIMEOUT:
            case DbInitState.FAILED:
            case DbInitState.SCHEMA_INCOMPATIBLE:
                statusModel.AddError(
                    notification.Failure?.DefaultMessage ?? localizer["Status_Failed"],
                    nameof(DemoDbInitNotifier));
                break;
            case DbInitState.READY:
                statusModel.CancelQueuedMessage();
                break;
        }

        return ValueTask.CompletedTask;
    }
}