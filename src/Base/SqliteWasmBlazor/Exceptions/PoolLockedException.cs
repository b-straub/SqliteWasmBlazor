// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Diagnostic exception thrown by <see cref="SqliteWasmWorkerBridge"/> when
/// a database operation reaches the bridge while the encrypted pool is in the
/// <c>EncryptedPoolState.Encrypted</c>+locked state. Named without a cref on
/// purpose: that type lives in the Crypto plane, which this plane cannot
/// reference.
///
/// <para>
/// <b>This exception means consumer code reached EF outside an
/// <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c> gate.</b> Component
/// lifecycle hooks (<c>OnInitialized*</c>, <c>OnContextReadyAsync</c>,
/// <c>OnAfterRender*</c>) run at component construction regardless of
/// authorization policy state — they are the wrong place for any DB touch.
/// The fix is in the consumer's reactive wiring, not in catching this
/// exception. This is also the race-safe path during user-driven Lock —
/// an in-flight query that races the lock transition gets a clean
/// <see cref="InvalidOperationException"/> instead of reading ciphertext
/// as plain and corrupting EF state.
/// </para>
///
/// <para>
/// <b>Message is diagnostic, not user-facing.</b> User-facing copy is the
/// consumer's responsibility — recognize the type in your RxBlazorV2
/// command error formatter (the third positional argument of
/// <c>[ObservableCommand]</c>) and route to a localized resx key. The
/// <c>&lt;DatabaseInformationAlert/&gt;</c> on <c>ENCRYPTED_LOCKED</c> covers
/// the boot-state UX separately.
/// </para>
/// </summary>
public sealed class PoolLockedException : InvalidOperationException
{
    /// <summary>
    /// Creates the exception for a bridge operation that was reached while
    /// the pool was locked.
    /// </summary>
    /// <param name="operation">
    /// The operation that reached the bridge, named in the message so the
    /// offending call site is identifiable from the log alone.
    /// </param>
    public PoolLockedException(string operation)
        : base($"Encrypted pool is locked — '{operation}' reached the bridge without an unlock. " +
               $"Consumer code likely touched DbContext outside <AuthorizeView Policy=\"DatabaseOpen\">. " +
               $"Move DB-touching work into a path gated by DbStateModel.State == READY (e.g. a model-side " +
               $"OnDbStateChangedAsync internal observer that self-guards before each EF call). " +
               $"In RxBlazorV2 commands, recognize this exception type in the command's error formatter " +
               $"and route to a localized resx key for the user-facing message.")
    {
    }
}
