// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Diagnostic exception thrown by <see cref="SqliteWasmWorkerBridge"/> when
/// a database operation reaches the bridge while the encrypted VFS disk is
/// in the <see cref="EncryptedDiskState.Encrypted"/>+locked state.
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
/// <c>&lt;DatabaseErrorAlert/&gt;</c> on <c>ENCRYPTED_LOCKED</c> covers
/// the boot-state UX separately.
/// </para>
/// </summary>
public sealed class DiskLockedException : InvalidOperationException
{
    public DiskLockedException(string operation)
        : base($"Encrypted VFS disk is locked — '{operation}' reached the bridge without an unlock. " +
               $"Consumer code likely touched DbContext outside <AuthorizeView Policy=\"DatabaseOpen\">. " +
               $"Move DB-touching work into a path gated by DbStateModel.State == READY (e.g. a model-side " +
               $"OnDbStateChangedAsync internal observer that self-guards before each EF call). " +
               $"In RxBlazorV2 commands, recognize this exception type in the command's error formatter " +
               $"and route to a localized resx key for the user-facing message.")
    {
    }
}
