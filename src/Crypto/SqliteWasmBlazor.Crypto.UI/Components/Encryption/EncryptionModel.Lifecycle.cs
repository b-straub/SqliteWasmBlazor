namespace SqliteWasmBlazor.Crypto.UI.Components.Encryption;

/// <summary>
/// Backing model for the encrypted-VFS disk-management surface. Lives in
/// <c>Crypto.UI</c>; consumers compose their own page UI around the
/// commands (the demo's <c>Pages/DatabaseEncryption.razor</c> is one such
/// composition). JSInterop-free — downloads happen entirely worker-side
/// via the streaming export primitives (the worker emits a Blob and the
/// bridge clicks an anchor); the model never holds the bytes.
///
/// <para>Page branches by <see cref="EncryptedPoolState"/>: Plain ⇒ Encrypt
/// button; Encrypted+Unlocked ⇒ Lock / Leave + per-DB export/import;
/// Encrypted+Locked ⇒ short-lived while the lifecycle auto-unlock completes.
/// <see cref="Reset"/> renders outside those branches — a disk whose passkey
/// is gone has no other way out, and it is the one command with no
/// CanExecute gate. <c>AuthenticationPanel</c> renders below whenever the
/// disk is not Encrypted+Unlocked.</para>
///
/// <para>Reactivity: auto-detected observers on <c>Auth.PublicKey</c> and
/// <c>DbState.State</c> re-run <see cref="RefreshAsync"/> on sign-in/out,
/// Lock, Reset, and lifecycle Unlock.</para>
/// </summary>
public partial class EncryptionModel
{
    protected override async Task OnContextReadyAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
    }

    // Auto-detected internal observer keyed on Auth.PublicKey.
    private async Task OnAuthChangedAsync(CancellationToken cancellationToken)
    {
        _ = Auth.PublicKey;
        await RefreshAsync(cancellationToken);
    }

    // Auto-detected internal observer keyed on DbState.State.
    private async Task OnDbStateChangedAsync(CancellationToken cancellationToken)
    {
        _ = DbState.State;
        await RefreshAsync(cancellationToken);
    }
}
