using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using R3;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

/// <summary>
/// Reactive model behind <see cref="AuthenticationPanel"/> — the single
/// Crypto.UI panel for "sign in with your passkey, or register one if you
/// don't have one yet". Mirrors <c>BlazorPRF.UI.Models.PrfModel</c> shape
/// 1:1 (smart Authenticate routing + DiscoverableCancelled fallback +
/// inline Register) and is the only writer to
/// <see cref="PrfAuthenticationStateProvider"/> for users coming through
/// the panel. The consumer wraps a plain
/// <c>&lt;AuthorizeView&gt;</c> around its content; this panel goes in the
/// <c>&lt;NotAuthorized&gt;</c> branch.
///
/// <para>
/// <b>State machine.</b>
/// <list type="bullet">
///   <item><b>Anonymous, hint set, !DiscoverableCancelled.</b> "Authenticate"
///         targets the hinted credential via
///         <see cref="DeriveKeys"/>. Cancel of a hinted ceremony clears the
///         hint and falls through to discoverable.</item>
///   <item><b>Anonymous, no hint, !DiscoverableCancelled.</b> "Authenticate"
///         opens the platform discoverable picker via
///         <see cref="DeriveKeysDiscoverable"/>.</item>
///   <item><b>Anonymous, DiscoverableCancelled.</b> User dismissed the
///         discoverable picker — UI shows the inline Register section + a
///         "Try again" button. <see cref="Register"/> takes the optional
///         display name and runs the Stage 3.a-1 register-then-derive
///         contract; on success the user is signed in directly.</item>
///   <item><b>TTL expiry.</b> <see cref="IPrfService.KeyExpired"/> filtered
///         on the seed cache key fires <see cref="OnSessionExpired"/>; both
///         <see cref="CredentialId"/> and <see cref="PublicKey"/> clear,
///         and the trigger pushes Anonymous through the state provider.
///         The consumer's <c>&lt;AuthorizeView&gt;</c> snaps back to
///         NotAuthorized so the panel reappears.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Trigger contract.</b> <see cref="CredentialId"/> + <see cref="PublicKey"/>
/// each carry <c>[ObservableTrigger(nameof(PushAuthState))]</c>; the trigger
/// pushes a snapshot through <see cref="PrfAuthenticationStateProvider"/>
/// which raises Blazor's standard <c>NotifyAuthenticationStateChanged</c>
/// event consumed by <c>&lt;CascadingAuthenticationState&gt;</c>.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Singleton)]
[ObservableComponent]
public partial class AuthenticationModel : ObservableModel
{
    public partial AuthenticationModel(
        IPrfAuthenticator authenticator,
        IPasskeyHintProvider hintProvider,
        IPrfService prfService,
        IOptions<PrfOptions> prfOptions,
        PrfAuthenticationStateProvider stateProvider,
        StatusModel statusModel,
        IStringLocalizer<AuthenticationModel> localizer);

    /// <summary>
    /// PRF support probe result. <c>null</c> while checking; <c>true</c> /
    /// <c>false</c> after the boot probe in <see cref="OnContextReadyAsync"/>.
    /// </summary>
    public partial bool? IsPrfSupported { get; set; }

    /// <summary>
    /// Last-registered credential id loaded from <see cref="IPasskeyHintProvider"/>
    /// on context-ready. Drives the smart-routing branch in the panel UI:
    /// non-empty → "Authenticate" uses <see cref="DeriveKeys"/> (direct);
    /// empty → uses <see cref="DeriveKeysDiscoverable"/>.
    /// Persists across sessions via the host-supplied hint provider.
    /// </summary>
    [ObservableTrigger(nameof(PushAuthState))]
    public partial string? CredentialId { get; set; }

    /// <summary>
    /// Active PRF-derived X25519 public key (Base64). Together with
    /// <see cref="CredentialId"/> this is the authoritative session state
    /// the panel pushes through <see cref="PrfAuthenticationStateProvider"/>.
    /// </summary>
    [ObservableTrigger(nameof(PushAuthState))]
    public partial string? PublicKey { get; set; }

    /// <summary>
    /// Set when a discoverable-credential ceremony returns null (user
    /// dismissed). Switches the panel UI to the "no passkey selected,
    /// register one or try again" branch.
    /// </summary>
    public partial bool DiscoverableCancelled { get; set; }

    /// <summary>
    /// User-supplied display name for the inline register form. Empty →
    /// platform shows the default RP name in the credential picker.
    /// </summary>
    public partial string? RegisterDisplayName { get; set; }

    [ObservableCommand(nameof(DeriveKeysAsync), nameof(CanDeriveKeys), nameof(FormatAuthenticateError))]
    public partial IObservableCommandAsync DeriveKeys { get; }

    [ObservableCommand(nameof(DeriveKeysDiscoverableAsync), nameof(CanDeriveKeysDiscoverable), nameof(FormatAuthenticateError))]
    public partial IObservableCommandAsync DeriveKeysDiscoverable { get; }

    [ObservableCommand(nameof(RegisterAsync), nameof(CanRegister), nameof(FormatRegisterError))]
    public partial IObservableCommandAsync Register { get; }

    [ObservableCommand(nameof(TryAgain))]
    public partial IObservableCommand TryAgainCommand { get; }

    /// <summary>
    /// Unified session-clear: drops the JS-side PRF cache and the model's
    /// local <see cref="PublicKey"/>. <see cref="CredentialId"/> is kept as
    /// the hint for the next direct-auth attempt. <see cref="PushAuthState"/>
    /// fires through the trigger so the consumer's <c>AuthorizeView</c>
    /// snaps back to NotAuthorized.
    /// </summary>
    [ObservableCommand(nameof(ClearKeys))]
    public partial IObservableCommand ClearKeysCommand { get; }

    private bool CanDeriveKeys() => IsPrfSupported == true && !string.IsNullOrWhiteSpace(CredentialId);
    private bool CanDeriveKeysDiscoverable() => IsPrfSupported == true;
    private bool CanRegister() => IsPrfSupported == true;

    protected override async Task OnContextReadyAsync()
    {
        IsPrfSupported = await Authenticator.CheckPrfSupportAsync();
        if (IsPrfSupported != true)
        {
            return;
        }

        CredentialId = await HintProvider.GetCredentialIdAsync();

        // Re-hydrate from a still-warm PRF cache so the panel surfaces the
        // authenticated state without a redundant WebAuthn round-trip.
        if (PrfService.HasCachedKeys() && PrfService.GetCachedPublicKey() is { Length: > 0 } cachedPub)
        {
            PublicKey = cachedPub;
        }

        // Session-end subscription: TTL elapse / explicit ClearKeys → clear state.
        var seedKey = $"prf-seed:{PrfOptions.Value.Salt}";
        Subscriptions.Add(
            PrfService.KeyExpired
                .Where(cacheKey => cacheKey == seedKey)
                .Subscribe(_ => OnSessionExpired()));
    }

    /// <summary>
    /// Direct authenticate against the hinted credential. Cancel returns
    /// null → clear the hint + flag <see cref="DiscoverableCancelled"/> so
    /// the panel offers the discoverable picker / register fallback. WebAuthn
    /// errors throw <see cref="PrfAuthenticatorException"/>; the per-command
    /// formatter localizes via <c>Error_Authenticate_{code}</c>.
    /// </summary>
    private async Task DeriveKeysAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CredentialId))
        {
            return;
        }

        var result = await Authenticator.AuthenticateAsync(CredentialId, cancellationToken);
        if (result is null)
        {
            // Targeted ceremony cancelled — hint may be stale; clear it and
            // surface the discoverable / register fork.
            await HintProvider.ClearAsync(cancellationToken);
            CredentialId = null;
            DiscoverableCancelled = true;
            StatusModel.AddWarning(Localizer["Status_AuthenticationCancelled"], nameof(DeriveKeys));
            return;
        }

        await ApplySessionAsync(result.CredentialId, result.PublicKeyBase64, cancellationToken);
    }

    /// <summary>
    /// Discoverable credential picker. Cancel sets
    /// <see cref="DiscoverableCancelled"/> so the panel renders the inline
    /// Register fallback alongside "Try again".
    /// </summary>
    private async Task DeriveKeysDiscoverableAsync(CancellationToken cancellationToken)
    {
        DiscoverableCancelled = false;

        var result = await Authenticator.AuthenticateAsync(null, cancellationToken);
        if (result is null)
        {
            DiscoverableCancelled = true;
            StatusModel.AddWarning(Localizer["Status_DiscoverableCancelled"], nameof(DeriveKeysDiscoverable));
            return;
        }

        await ApplySessionAsync(result.CredentialId, result.PublicKeyBase64, cancellationToken);
    }

    /// <summary>
    /// Register a new passkey + immediate-derive (Stage 3.a-1 contract).
    /// On success the user lands directly in the authenticated state — no
    /// follow-up Authenticate click. Cancel of either WebAuthn ceremony
    /// throws <see cref="OperationCanceledException"/> → routed to
    /// <see cref="FormatRegisterError"/> for friendly status text.
    /// </summary>
    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var displayName = string.IsNullOrWhiteSpace(RegisterDisplayName) ? null : RegisterDisplayName.Trim();
        var result = await Authenticator.RegisterAsync(displayName, cancellationToken);

        await ApplySessionAsync(result.CredentialId, result.PublicKeyBase64, cancellationToken);

        RegisterDisplayName = null;
        DiscoverableCancelled = false;
        StatusModel.AddSuccess(Localizer["Status_Registered"], nameof(Register));
    }

    private void TryAgain()
    {
        DiscoverableCancelled = false;
    }

    private void ClearKeys()
    {
        PrfService.ClearKeys();
        PublicKey = null;
        // CredentialId intentionally retained — acts as the hint for the
        // next direct-auth attempt (BlazorPRF behavior).
    }

    /// <summary>
    /// Apply a freshly-derived session. Each setter fires the
    /// <see cref="PushAuthState"/> trigger which pushes the snapshot through
    /// <see cref="PrfAuthenticationStateProvider"/>; the hint provider
    /// gets the new credential id so the next visit hits the direct path.
    /// </summary>
    private async Task ApplySessionAsync(
        string credentialId,
        string publicKeyBase64,
        CancellationToken cancellationToken)
    {
        CredentialId = credentialId;
        PublicKey = publicKeyBase64;
        DiscoverableCancelled = false;
        await HintProvider.SetCredentialIdAsync(credentialId, cancellationToken);
    }

    /// <summary>
    /// TTL elapsed / explicit ClearKeys: clear session state.
    /// <see cref="PushAuthState"/> fires per setter, eventually pushing
    /// Anonymous so consumer <c>AuthorizeView</c> snaps back to
    /// NotAuthorized.
    /// </summary>
    private void OnSessionExpired()
    {
        PublicKey = null;
        // Keep CredentialId as the hint for the next direct-auth attempt.
    }

    /// <summary>
    /// Trigger target for <see cref="CredentialId"/> / <see cref="PublicKey"/>.
    /// Single point that pushes auth state through
    /// <see cref="PrfAuthenticationStateProvider"/>.
    /// </summary>
    private void PushAuthState()
    {
        StateProvider.UpdateAuthenticationState(CredentialId, PublicKey);
    }

    private string FormatAuthenticateError(Exception ex) => ex switch
    {
        PrfAuthenticatorException { Operation: PrfAuthenticatorOperation.Authenticate, Code: var code } =>
            Localizer[$"Error_Authenticate_{code}"],
        OperationCanceledException => Localizer["Status_AuthenticationCancelled"],
        _ => Localizer["Error_Authenticate_Unknown", ex.Message],
    };

    private string FormatRegisterError(Exception ex) => ex switch
    {
        PrfAuthenticatorException { Operation: PrfAuthenticatorOperation.Register, Code: var code } =>
            Localizer[$"Error_Register_{code}"],
        OperationCanceledException => Localizer["Status_RegisterCancelled"],
        _ => Localizer["Error_Register_Unknown", ex.Message],
    };
}
