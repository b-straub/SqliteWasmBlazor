namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Host-supplied seam carrying the WebAuthn-PRF authentication / key
/// derivation pipeline behind <see cref="Components.Authentication.AuthenticationPanel"/>
/// and <see cref="Components.Authentication.RegistrationPanel"/>. The
/// CryptoSync.UI library does not register a default implementation —
/// the consumer wires either a stub (test fixtures) or the production
/// PRF-backed implementation that lands in the post-Stage-2 demo step.
///
/// <para>
/// Implementations must be safe to call from a Blazor render context
/// (typically driving JS interop into the browser's WebAuthn API).
/// </para>
/// </summary>
public interface IPrfAuthenticator
{
    /// <summary>
    /// Probe whether the current platform / browser supports the WebAuthn
    /// PRF extension. Called once on panel ready to gate the rest of the
    /// UI surface.
    /// </summary>
    ValueTask<bool> CheckPrfSupportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new WebAuthn credential with PRF support and return the
    /// credential identifier plus the X25519 public key derived from the
    /// PRF output. <paramref name="displayName"/> is shown in the platform's
    /// credential UI.
    /// </summary>
    ValueTask<PrfRegistrationResult> RegisterAsync(
        string? displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run a WebAuthn assertion against an existing credential and return
    /// the derived public key. Pass a non-null <paramref name="credentialIdHint"/>
    /// to target a specific credential; pass <c>null</c> to use the
    /// platform's discoverable-credential picker.
    /// Returns <c>null</c> if the user dismissed the prompt.
    /// </summary>
    ValueTask<PrfAuthenticationResult?> AuthenticateAsync(
        string? credentialIdHint,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IPrfAuthenticator.RegisterAsync"/>. The
/// <paramref name="CredentialId"/> is opaque to the panel and persisted by
/// the host so subsequent <see cref="IPrfAuthenticator.AuthenticateAsync"/>
/// calls can pass it back as a hint.
/// </summary>
public sealed record PrfRegistrationResult(
    string CredentialId,
    string PublicKeyBase64);

/// <summary>
/// Result of <see cref="IPrfAuthenticator.AuthenticateAsync"/>. Mirrors
/// <see cref="PrfRegistrationResult"/> — the credential id may differ from
/// the hint when the discoverable-credential picker chose a different one.
/// </summary>
public sealed record PrfAuthenticationResult(
    string CredentialId,
    string PublicKeyBase64);
