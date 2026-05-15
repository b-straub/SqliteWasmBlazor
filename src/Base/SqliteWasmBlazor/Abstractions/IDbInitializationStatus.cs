// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Read-only view of the current database boot state. Razor consumers
/// inject this and switch on <see cref="State"/> / pattern-match on
/// <see cref="Failure"/> to render the right UI for each failure mode.
///
/// <para>
/// <b>Reactivity.</b> The Crypto.UI binding for this interface is the
/// <c>DbStateModel</c> ObservableModel (Singleton) — change reactivity is
/// declarative through RxBlazorV2 attributes (auto-detected internal
/// observers when injecting <c>DbStateModel</c> into another
/// ObservableModel; <c>[ObservableModelObserver]</c> on services). No
/// public <c>event</c> here: the seam is read-on-demand for hosts that
/// don't take the Crypto.UI dep, reactive for hosts that do.
/// </para>
/// </summary>
public interface IDbInitializationStatus
{
    /// <summary>Current lifecycle stage. <see cref="DbInitState.READY"/> when boot succeeded.</summary>
    DbInitState State { get; }

    /// <summary>
    /// Structured failure payload. Non-null whenever <see cref="State"/> is
    /// not <see cref="DbInitState.READY"/> and not
    /// <see cref="DbInitState.NOT_STARTED"/>/<see cref="DbInitState.INITIALIZING"/>.
    /// Concrete record types live in this assembly (see <c>DbInitFailures.cs</c>)
    /// or in downstream packages that opt into the unified surface.
    /// </summary>
    IDbInitFailure? Failure { get; }
}
