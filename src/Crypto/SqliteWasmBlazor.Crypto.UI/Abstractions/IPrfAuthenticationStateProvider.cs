namespace SqliteWasmBlazor.Crypto.UI.Abstractions;

/// <summary>
/// PRF-specific push surface on top of Blazor's <c>AuthenticationStateProvider</c>.
/// Models (<c>AuthenticationModel</c>, <c>DbStateModel</c>) inject this interface
/// instead of the concrete provider so the implementation can stay internal to
/// <c>SqliteWasmBlazor.Crypto.UI</c>.
/// </summary>
public interface IPrfAuthenticationStateProvider
{
    /// <summary>
    /// Push the latest credential id + X25519 pubkey snapshot and re-fire the
    /// framework's <c>NotifyAuthenticationStateChanged</c>. Called from
    /// <c>AuthenticationModel</c>'s <c>PushAuthState</c> trigger method.
    /// </summary>
    void UpdateAuthenticationState(string? credentialId, string? publicKey);

    /// <summary>
    /// Push the latest boot DB state and re-fire
    /// <c>NotifyAuthenticationStateChanged</c> so every
    /// <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c> in the tree
    /// re-evaluates. Called from <c>DbStateModel</c>'s <c>PushDbStateClaim</c>
    /// trigger method.
    /// </summary>
    void UpdateDbState(DbInitState state);
}
