using R3;
using RxBlazorV2.Model;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

/// <summary>
/// Reactive model behind <see cref="AuthenticationPanel"/>: the
/// <c>NotAuthorized</c>-branch panel for sign-in / register with a passkey.
/// Sole writer to <see cref="PrfAuthenticationStateProvider"/> for the auth-
/// panel flow.
///
/// <para>State branches: hinted-credential targeted auth (falls through to
/// the discoverable picker on cancel) → discoverable picker → inline register
/// when the picker is dismissed. TTL expiry (<see cref="IPrfService.KeyExpired"/>
/// filtered on the seed key) fires <see cref="OnSessionExpiredAsync"/>, which
/// clears <see cref="PublicKey"/> and disambiguates "TTL on still-bound disk"
/// from "disk reset" by reading the manifest hint.</para>
///
/// <para><see cref="CredentialId"/> + <see cref="PublicKey"/> each fire
/// <see cref="PushAuthState"/>, which is the single point that updates
/// <see cref="PrfAuthenticationStateProvider"/>.</para>
/// </summary>
public partial class AuthenticationModel
{
    protected override async Task OnContextReadyAsync()
    {
        IsPrfSupported = await Authenticator.CheckPrfSupportAsync();
        if (IsPrfSupported != true)
        {
            return;
        }

        var diskState = await Session.GetStateAsync();
        CredentialId = diskState.Hint;

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
    // panel render opens the discoverable picker). Disambiguate via the
    // manifest-hint read.
    private async ValueTask OnSessionExpiredAsync()
    {
        PublicKey = null;

        var diskState = await Session.GetStateAsync();
        if (string.IsNullOrEmpty(diskState.Hint))
        {
            CredentialId = null;
        }
    }
}
