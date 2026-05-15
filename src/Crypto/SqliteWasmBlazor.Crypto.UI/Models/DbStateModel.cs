using RxBlazorV2.Model;
using SqliteWasmBlazor.Crypto.UI.Abstractions;

namespace SqliteWasmBlazor.Crypto.UI;

/// <summary>
/// Reactive boot/lifecycle DB state. Singleton ObservableModel that
/// implements both the writer-side
/// <see cref="IDbInitializationReporter"/> seam (called by base-package
/// boot probes and <c>EncryptedSqliteWasmDatabaseService.ReportDbState</c>) and the
/// reader-side <see cref="IDbInitializationStatus"/> seam consumed by UI
/// models.
///
/// <para>
/// <b>Why a model and not a service.</b> The pre-fork
/// <c>DbInitializationService</c> exposed a plain <c>event Action?
/// Changed</c>. Every UI consumer that wanted to react had to
/// <c>+=</c> the event AND bridge it through <c>InvokeAsync</c> to keep
/// re-rendering — an anti-pattern that surfaces as
/// <see cref="ObjectDisposedException"/> when the renderer dispatcher
/// races a long-running command (the
/// <c>EnterEncrypted → Lock</c> burst incident). Promoting the state
/// holder to an <see cref="ObservableModel"/> means consumers reach it
/// through:
/// <list type="bullet">
///   <item>auto-detected internal observers (a private method on a
///         downstream model that accesses <c>DbState.State</c> /
///         <c>DbState.Failure</c> is wired automatically by the SG); or</item>
///   <item><see cref="ObservableModelObserverAttribute"/> on a
///         service-side method.</item>
/// </list>
/// Either way: zero <c>event +=</c>, zero
/// <c>Subscriptions.Add(observable.Subscribe(...))</c>, zero
/// <c>InvokeAsync</c> bridges in user code.
/// </para>
///
/// <para>
/// Registered by <c>AddCryptoUI</c> via
/// <c>ObservableModels.Initialize</c> (Singleton) and bound to the two
/// base-package interfaces via <c>services.Replace</c>. Hosts that don't
/// reference Crypto.UI keep the plain <c>DbInitializationService</c>
/// from base — they don't need reactive primitives.
/// </para>
///
/// <para>
/// <b>Side-effect bridge to <see cref="PrfAuthenticationStateProvider"/>.</b>
/// State changes need to flip <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c>
/// (Blazor's auth-state cascade is the only way to re-evaluate that gate),
/// so the <c>State</c> setter fires an <see cref="ObservableTriggerAttribute"/>
/// that pushes the new value into the provider via a one-way
/// <c>UpdateDbState</c> method. The provider does not inject the model;
/// the model injects the provider — keeps the DI graph acyclic.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Singleton)]
public partial class DbStateModel
    : ObservableModel, IDbInitializationStatus, IDbInitializationReporter
{
    public partial DbStateModel(IPrfAuthenticationStateProvider authProvider);

    [ObservableTrigger(nameof(PushDbStateClaim))]
    public partial DbInitState State { get; set; } = DbInitState.NOT_STARTED;

    public partial IDbInitFailure? Failure { get; set; }

    /// <summary>
    /// Writer-side seam called by base-package boot probes and
    /// <c>EncryptedSqliteWasmDatabaseService.ReportDbState</c>. Idempotent — a same-state
    /// re-report is dropped before the trigger pipeline fires, so
    /// <c>EnterEncrypted</c>'s repeated <c>READY</c> reports don't spam
    /// the auth-state cascade.
    /// </summary>
    public void Report(DbInitState state, IDbInitFailure? failure = null)
    {
        if (state == State && ReferenceEquals(failure, Failure))
        {
            return;
        }
        using (SuspendNotifications())
        {
            State = state;
            Failure = failure;
        }
    }

    /// <summary>
    /// Mirrors the latest <see cref="State"/> into
    /// <see cref="PrfAuthenticationStateProvider"/> so
    /// <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c> re-evaluates.
    /// One-way — the provider holds the value as a private field; no
    /// reverse dependency.
    /// </summary>
    private void PushDbStateClaim() => AuthProvider.UpdateDbState(State);
}
