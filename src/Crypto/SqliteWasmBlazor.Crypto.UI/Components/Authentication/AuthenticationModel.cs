using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI.Abstractions;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

// Commands + state for the auth panel. Lifecycle hooks + the state-machine
// documentation live in the .Lifecycle.cs partial sibling.
[ObservableModelScope(ModelScope.Singleton)]
[ObservableComponent]
public partial class AuthenticationModel : ObservableModel
{
    public partial AuthenticationModel(
        IPrfAuthenticator authenticator,
        IEncryptedSqliteWasmDatabaseService session,
        IPrfService prfService,
        IOptions<PrfOptions> prfOptions,
        IPrfAuthenticationStateProvider stateProvider,
        StatusModel statusModel,
        IStringLocalizer<AuthenticationModel> localizer);

    // null while checking; bool after the OnContextReadyAsync probe.
    public partial bool? IsPrfSupported { get; set; }

    // Disk-bound hint loaded from the manifest. Non-empty → targeted
    // DeriveKeys; empty → discoverable picker.
    [ObservableTrigger(nameof(PushAuthState))]
    public partial string? CredentialId { get; set; }

    [ObservableTrigger(nameof(PushAuthState))]
    public partial string? PublicKey { get; set; }

    // User dismissed the discoverable picker — flips the panel into the
    // "register + try again" branch.
    public partial bool DiscoverableCancelled { get; set; }

    public partial string? RegisterDisplayName { get; set; }

    // Pubkey of a passkey that authenticated but didn't match the disk's
    // hint. Session rejected; exposed so the user can copy it for an
    // "export disk for recipient" round-trip to that passkey.
    public partial string? WrongPasskeyPublicKey { get; set; }

    public partial string? WrongPasskeyCredentialId { get; set; }

    [ObservableCommand(nameof(DeriveKeysAsync), nameof(CanDeriveKeys), nameof(FormatAuthenticateError))]
    public partial IObservableCommandAsync DeriveKeys { get; }

    [ObservableCommand(nameof(DeriveKeysDiscoverableAsync), nameof(CanDeriveKeysDiscoverable), nameof(FormatAuthenticateError))]
    public partial IObservableCommandAsync DeriveKeysDiscoverable { get; }

    [ObservableCommand(nameof(RegisterAsync), nameof(CanRegister), nameof(FormatRegisterError))]
    public partial IObservableCommandAsync Register { get; }

    private bool CanDeriveKeys() => IsPrfSupported == true && !string.IsNullOrWhiteSpace(CredentialId);
    private bool CanDeriveKeysDiscoverable() => IsPrfSupported == true;
    private bool CanRegister() => IsPrfSupported == true;

    // Direct authenticate against the hinted credential; on cancel falls
    // through to the discoverable picker in the same click. CredentialId is
    // intentionally retained — it's the VFS-encryption marker per SoT, not
    // a "last used credential" cache. WebAuthn errors throw
    // PrfAuthenticatorException → FormatAuthenticateError.
    private async Task DeriveKeysAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CredentialId))
        {
            return;
        }

        var result = await Authenticator.AuthenticateAsync(CredentialId, cancellationToken);
        if (result is null)
        {
            await DeriveKeysDiscoverableAsync(cancellationToken);
            return;
        }

        await ApplySessionAsync(result.CredentialId, result.PublicKeyBase64);
    }

    // Discoverable credential picker. Cancel sets DiscoverableCancelled so
    // the panel renders the inline Register fallback + Try again.
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

        await ApplySessionAsync(result.CredentialId, result.PublicKeyBase64);
    }

    // Register a new passkey + immediate-derive (Stage 3.a-1 contract).
    // Does NOT touch the disk manifest — that's EnterEncryptedAsync's job;
    // writing the manifest here would flip the VFS to Encrypted while on-
    // disk databases are still plaintext, stranding the user out of the
    // encryption plane.
    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var displayName = string.IsNullOrWhiteSpace(RegisterDisplayName) ? null : RegisterDisplayName.Trim();
        var result = await Authenticator.RegisterAsync(displayName, cancellationToken);

        if (!await ApplySessionAsync(result.CredentialId, result.PublicKeyBase64))
        {
            return;
        }

        RegisterDisplayName = null;
        DiscoverableCancelled = false;
        StatusModel.AddSuccess(Localizer["Status_Registered"], nameof(Register));
    }

    // Drops the JS PRF cache + PublicKey; keeps CredentialId as the hint
    // for the next direct-auth attempt (BlazorPRF behavior).
    public void ClearKeys()
    {
        PrfService.ClearKeys();
        PublicKey = null;
    }

    // Full sign-out — also drops CredentialId so the next render opens the
    // discoverable picker. On an Encrypted disk OnContextReadyAsync re-
    // hydrates CredentialId from the manifest hint, so this is a no-op for
    // the disk binding.
    public void SignOut()
    {
        PrfService.ClearKeys();
        PublicKey = null;
        CredentialId = null;
        DiscoverableCancelled = false;
        RegisterDisplayName = null;
        WrongPasskeyPublicKey = null;
        WrongPasskeyCredentialId = null;
    }

    // Apply a freshly-derived session. Returns false when refused for a
    // disk-mismatch (wrong passkey for this disk's manifest hint) — caller
    // must skip post-success bookkeeping. Refusing here turns "wrong
    // passkey" into a clean status warning instead of an SQLITE_IOERR deep
    // in EF Core after the AuthorizeView flips Authorized under an unfit
    // VFS key. Does NOT write the manifest — that's EnterEncrypted's job.
    private async ValueTask<bool> ApplySessionAsync(string credentialId, string publicKeyBase64)
    {
        var diskState = await Session.GetStateAsync();
        var diskHint = diskState.Hint;
        if (!string.IsNullOrEmpty(diskHint) &&
            !string.Equals(diskHint, credentialId, StringComparison.Ordinal))
        {
            PrfService.ClearKeys();
            // Expose the rejected pubkey for the user to copy — useful for
            // "export disk for recipient" against the other passkey.
            WrongPasskeyPublicKey = publicKeyBase64;
            WrongPasskeyCredentialId = credentialId;
            StatusModel.AddWarning(
                Localizer["Status_WrongPasskeyForDisk"],
                nameof(DeriveKeysDiscoverable));
            return false;
        }

        CredentialId = credentialId;
        PublicKey = publicKeyBase64;
        DiscoverableCancelled = false;
        WrongPasskeyPublicKey = null;
        WrongPasskeyCredentialId = null;
        return true;
    }

    public void DismissWrongPasskey()
    {
        WrongPasskeyPublicKey = null;
        WrongPasskeyCredentialId = null;
    }

    /// <summary>
    /// Apply a session derived during the guided disk-import flow. Bypasses
    /// the wrong-passkey-for-disk guard in <see cref="ApplySessionAsync"/>
    /// because the caller has just rebound the disk's manifest to this
    /// credential as part of the same atomic operation — the guard would
    /// reject the (now-correct) credential against the (pre-rebind) hint
    /// and orphan the auth state.
    /// </summary>
    public void ApplyImportedSession(string credentialId, string publicKeyBase64)
    {
        CredentialId = credentialId;
        PublicKey = publicKeyBase64;
        DiscoverableCancelled = false;
        WrongPasskeyPublicKey = null;
        WrongPasskeyCredentialId = null;
    }

    // Trigger target for CredentialId/PublicKey — single sink that pushes
    // auth state through PrfAuthenticationStateProvider.
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
