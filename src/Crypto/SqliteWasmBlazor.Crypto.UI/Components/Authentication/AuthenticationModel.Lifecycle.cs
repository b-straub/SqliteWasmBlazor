using R3;
using RxBlazorV2.Model;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

/// <summary>
/// Reactive model behind <see cref="AuthenticationPanel"/>: the
/// <c>NotAuthorized</c>-branch panel for sign-in / register with a passkey.
/// Sole writer to <see cref="Services.PrfAuthenticationStateProvider"/> for the auth-
/// panel flow.
///
/// <para>The panel has exactly two shapes, and the disk picks which one:
/// an encrypted VFS is bound to the credential its manifest names, so it
/// offers <see cref="SignIn"/> alone (targeted at that credential); a plain
/// VFS offers <see cref="SignIn"/> (discoverable picker) and
/// <see cref="Register"/> together. One click runs one ceremony — an
/// abandoned prompt reports a warning and leaves the shape untouched, so
/// cancelling always lands back where it started.</para>
///
/// <para><see cref="RefreshPoolStateAsync"/> is the single manifest read
/// behind that: it runs on context-ready, on TTL expiry
/// (<see cref="SqliteWasmBlazor.Crypto.Services.IPrfService.KeyExpired"/> filtered on the seed key →
/// <see cref="OnSessionExpiredAsync"/>), and on
/// <see cref="ClearKeysAsync"/> / <see cref="SignOutAsync"/> — every route
/// by which the panel becomes visible again. Disk reset therefore drops
/// <see cref="CredentialId"/> and a mid-session
/// <c>EnterEncrypted</c>/<c>LeaveEncrypted</c> is picked up too, without
/// either needing a special case.</para>
///
/// <para><see cref="CredentialId"/> + <see cref="PublicKey"/> each fire
/// <see cref="PushAuthState"/>, which is the single point that updates
/// <see cref="Services.PrfAuthenticationStateProvider"/>.</para>
/// </summary>
public partial class AuthenticationModel
{
    protected override async Task OnContextReadyAsync()
    {
        // This panel renders in the NotAuthorized branch of
        // <AuthorizeView Policy="DatabaseOpen">, which is what the tree shows
        // before initialization has reported anything — so it is typically the
        // first thing in the app to want the worker, and it renders before the
        // layout's <SqliteWasmDatabaseInitializer/> gets its OnAfterRenderAsync
        // (Blazor runs that child-before-parent). Without this await the
        // manifest read below reaches a worker with no SAHPool installed and
        // comes back "SQLite not initialized". Idempotent: whichever call
        // arrives first does the work.
        await Initializer.InitializeAsync();

        IsPrfSupported = await Authenticator.CheckPrfSupportAsync();
        if (IsPrfSupported != true)
        {
            return;
        }

        await RefreshPoolStateAsync();

        if (PrfService.HasCachedKeys() && PrfService.GetCachedPublicKey() is { Length: > 0 } cachedPub)
        {
            PublicKey = cachedPub;
        }

        // Canonical R3-event → model-state bridge for PrfService.KeyExpired.
        // PrfService is a Base-library service without RxBlazorV2 attributes,
        // so auto-detection can't reach it; this one hand-wired subscription
        // is the sanctioned bridge. Don't replicate the pattern — downstream
        // models react via the auto-detected observer on PublicKey/CredentialId.
        var seedKey = $"prf-seed:{PrfOptions.Value.Salt}";
        Subscriptions.Add(
            PrfService.KeyExpired
                .Where(cacheKey => cacheKey == seedKey)
                .SubscribeAwait(async (_, _) => await OnSessionExpiredAsync(),
                                AwaitOperation.Sequential));
    }

    // Two scenarios fire KeyExpired: TTL elapsed on a still-bound disk (keep
    // CredentialId as hint) vs. disk reset (drop CredentialId so the next
    // panel render opens the discoverable picker). The manifest read tells
    // them apart without either being special-cased here.
    private async ValueTask OnSessionExpiredAsync()
    {
        PublicKey = null;
        await RefreshPoolStateAsync();
    }

    // Manifest → panel state. CredentialId is the disk's binding, not a
    // "last used credential" cache, so it is read from the manifest rather
    // than remembered; PoolEncrypted decides whether register is on offer at
    // all. Only called while no session is active — an active session owns
    // CredentialId (it may be a plain disk's in-memory credential, which the
    // manifest knows nothing about until EnterEncrypted writes it).
    private async ValueTask RefreshPoolStateAsync()
    {
        var poolState = await Session.GetStateAsync();
        PoolEncrypted = poolState.Encrypted;
        CredentialId = poolState.Hint;
    }
}
