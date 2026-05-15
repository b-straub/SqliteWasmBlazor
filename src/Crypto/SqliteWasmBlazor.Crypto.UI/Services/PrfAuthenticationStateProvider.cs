using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SqliteWasmBlazor.Crypto.UI.Abstractions;

namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Blazor <see cref="AuthenticationStateProvider"/> backed by two
/// independent inputs that are both pushed in via one-way setters:
/// <list type="bullet">
///   <item>The PRF identity claim — set via
///         <see cref="UpdateAuthenticationState"/> by
///         <see cref="Components.Authentication.AuthenticationModel"/>'s
///         <c>PushAuthState</c> trigger.</item>
///   <item>The DatabaseState claim — set via <see cref="UpdateDbState"/>
///         by <see cref="DbStateModel"/>'s <c>PushDbStateClaim</c>
///         trigger.</item>
/// </list>
///
/// <para>
/// <b>Why one-way push, not event subscription.</b> The provider is a
/// terminal Blazor seam — <see cref="NotifyAuthenticationStateChanged"/>
/// is the framework's pre-defined notify channel and there's no model
/// upstream of it. Subscribing to <see cref="DbStateModel"/> events
/// here would either need an explicit R3 subscription (forbidden in
/// reactive components/services) or create a circular DI graph (model
/// already injects provider). Push-from-trigger keeps the dependency
/// arrow pointing one way: model → provider.
/// </para>
///
/// <para>
/// <b>Policies (registered by AddCryptoUI):</b>
/// <list type="bullet">
///   <item><c>DatabaseOpen</c> — requires <c>DatabaseState=OPEN</c>. Pages
///         that touch the DB wrap their content in
///         <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c>; the
///         NotAuthorized branch typically renders
///         <c>&lt;AuthenticationPanel/&gt;</c> so the user can sign in to
///         unlock an encrypted DB. Plain DBs always satisfy this policy
///         (boot init reports READY immediately).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class PrfAuthenticationStateProvider : AuthenticationStateProvider, IPrfAuthenticationStateProvider
{
    /// <summary>Claim type carrying the boot DB state for the
    /// <c>DatabaseOpen</c> policy.</summary>
    public const string DatabaseStateClaim = "DatabaseState";

    /// <summary>Claim value for <see cref="DbInitState.READY"/> — the only
    /// state that satisfies the <c>DatabaseOpen</c> policy.</summary>
    public const string DatabaseStateOpen = "OPEN";

    /// <summary>Claim value for <see cref="DbInitState.ENCRYPTED_LOCKED"/>
    /// — surfaced for completeness; the absence of <c>OPEN</c> is what the
    /// policy actually rejects.</summary>
    public const string DatabaseStateLocked = "LOCKED";

    private string? _credentialId;
    private string? _publicKey;
    private DbInitState _dbState = DbInitState.NOT_STARTED;

    /// <summary>
    /// Pushes the latest credential id + X25519 pubkey snapshot from
    /// <see cref="Components.Authentication.AuthenticationModel"/> and
    /// fires <see cref="NotifyAuthenticationStateChanged"/>. Called from
    /// the model's <c>PushAuthState</c> trigger method.
    /// </summary>
    public void UpdateAuthenticationState(string? credentialId, string? publicKey)
    {
        _credentialId = credentialId;
        _publicKey = publicKey;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>
    /// Pushes the latest boot DB state from <see cref="DbStateModel"/>
    /// and fires <see cref="NotifyAuthenticationStateChanged"/> so every
    /// <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c> in the tree
    /// re-evaluates. Called from the model's <c>PushDbStateClaim</c>
    /// trigger method.
    /// </summary>
    public void UpdateDbState(DbInitState state)
    {
        _dbState = state;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var claims = new List<Claim>();

        // DB-state claim — drives the DatabaseOpen policy. Plain DBs report
        // READY out of boot; encrypted DBs report ENCRYPTED_LOCKED until
        // EncryptedDiskLifecycle's auto-unlock observes the auth identity and
        // unlocks the worker.
        switch (_dbState)
        {
            case DbInitState.READY:
                claims.Add(new Claim(DatabaseStateClaim, DatabaseStateOpen));
                break;
            case DbInitState.ENCRYPTED_LOCKED:
                claims.Add(new Claim(DatabaseStateClaim, DatabaseStateLocked));
                break;
            // NOT_STARTED / INITIALIZING / TAB_LOCKED / SCHEMA_INCOMPATIBLE /
            // TIMEOUT / FAILED — no DatabaseState claim. DatabaseOpen policy
            // fails; the standard DatabaseErrorAlert path covers the visual.
        }

        // PRF identity claims — only when a session is active.
        if (!string.IsNullOrEmpty(_publicKey))
        {
            claims.Add(new Claim(ClaimTypes.Name, "PRF user"));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, _publicKey));
            claims.Add(new Claim("CredentialId", _credentialId ?? string.Empty));
        }

        if (claims.Count == 0)
        {
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        }

        // Auth type non-null only when there's an identity (PublicKey set);
        // otherwise the principal is anonymous-but-claim-bearing so policy
        // checks can still pass on the DB-state claim alone.
        var authType = !string.IsNullOrEmpty(_publicKey) ? "PRF" : null;
        var identity = new ClaimsIdentity(claims, authType);
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}
