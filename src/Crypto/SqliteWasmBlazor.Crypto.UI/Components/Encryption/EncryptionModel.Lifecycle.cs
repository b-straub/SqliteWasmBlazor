namespace SqliteWasmBlazor.Crypto.UI.Components.Encryption;

/// <summary>
/// Backing model for the encrypted-VFS disk-management surface. Lives in
/// <c>Crypto.UI</c>; consumers compose their own page UI around the
/// commands (the demo's <c>Pages/DatabaseEncryption.razor</c> is one such
/// composition). JSInterop-free — downloads flow through the
/// <see cref="PendingDownload"/> component-trigger so the page partial owns
/// the file-save interop.
///
/// <para>Page wraps content in <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c>;
/// NotAuthorized renders <c>AuthenticationPanel</c>. Authorized branches by
/// <see cref="EncryptedDiskState"/>: Plain ⇒ Encrypt button; Encrypted+Unlocked ⇒
/// Lock / Leave / Reset + per-DB export/import; Encrypted+Locked ⇒ short-lived
/// while the lifecycle auto-unlock completes.</para>
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
