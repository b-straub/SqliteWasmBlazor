namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Host-supplied seam carrying the re-authentication / dismiss actions
/// behind <see cref="Components.Shared.SessionExpiredPopover"/>. The
/// CryptoSync.UI library does not register a default — the host wires
/// either a stub (test fixtures) or the production WebAuthn-backed
/// implementation that lands in the post-Stage-2 demo step.
/// </summary>
public interface ISessionAuthenticator
{
    /// <summary>
    /// Run the host's re-authentication flow (typically a WebAuthn
    /// assertion that re-derives the PRF-bound session key). The popover
    /// closes on successful return; exceptions surface through
    /// <see cref="IErrorModel"/> if registered.
    /// </summary>
    ValueTask ReAuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Run the host's dismiss-without-reauth flow (typically a navigation
    /// to a public landing page). The popover closes after the call returns.
    /// </summary>
    ValueTask DismissAsync(CancellationToken cancellationToken = default);
}
