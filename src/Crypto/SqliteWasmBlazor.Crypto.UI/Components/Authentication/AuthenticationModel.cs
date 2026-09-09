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
        DbStateModel dbState,
        IEncryptedSqliteWasmDatabaseService session,
        IPrfService prfService,
        IOptions<PrfOptions> prfOptions,
        IPrfAuthenticationStateProvider stateProvider,
        StatusModel statusModel,
        IStringLocalizer<AuthenticationModel> localizer);

    // null while checking; bool after the OnContextReadyAsync probe.
    public partial bool? IsPrfSupported { get; set; }

    // Disk-bound hint loaded from the manifest. Non-empty → targeted
    // ceremony against that credential; empty → discoverable picker.
    [ObservableTrigger(nameof(PushAuthState))]
    public partial string? CredentialId { get; set; }

    [ObservableTrigger(nameof(PushAuthState))]
    public partial string? PublicKey { get; set; }

    // Manifest says the VFS is encrypted. Decides the panel's shape: an
    // encrypted disk is bound to exactly one credential, so it offers sign-in
    // only — a second passkey derives a different PRF key and could never
    // unlock it. A plain disk offers sign-in and register side by side.
    public partial bool PoolEncrypted { get; set; }

    public partial string? RegisterDisplayName { get; set; }

    // Pubkey of a passkey that authenticated but didn't match the disk's
    // hint. Session rejected; exposed so the user can copy it for an
    // "export disk for recipient" round-trip to that passkey.
    public partial string? WrongPasskeyPublicKey { get; set; }

    public partial string? WrongPasskeyCredentialId { get; set; }

    [ObservableCommand(nameof(SignInAsync), nameof(CanSignIn), nameof(FormatAuthenticateError))]
    public partial IObservableCommandAsync SignIn { get; }

    [ObservableCommand(nameof(RegisterAsync), nameof(CanRegister), nameof(FormatRegisterError))]
    public partial IObservableCommandAsync Register { get; }

    private bool CanSignIn() => IsPrfSupported == true;

    // An encrypted disk unlocks with the credential its manifest names and
    // no other, so registering is not an option there.
    private bool CanRegister() => IsPrfSupported == true && !PoolEncrypted;

    // One ceremony per click. A hint (encrypted disk) targets the bound
    // credential; no hint (plain disk) opens the platform's discoverable
    // picker. CredentialId is intentionally retained — it's the VFS-
    // encryption marker per SoT, not a "last used credential" cache.
    //
    // An abandoned prompt reports and returns, leaving the panel exactly as
    // it was: the user lands back on the choices they started from instead
    // of on a different screen, and no second ceremony is chained behind the
    // first. WebAuthn errors throw PrfAuthenticatorException →
    // FormatAuthenticateError.
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        var hint = string.IsNullOrWhiteSpace(CredentialId) ? null : CredentialId;

        var outcome = await Authenticator.AuthenticateAsync(hint, cancellationToken);
        if (outcome.Result is null)
        {
            StatusModel.AddWarning(
                WithDetail(Localizer["Status_SignInCancelled"], outcome.Detail),
                nameof(SignIn));
            return;
        }

        await ApplySessionAsync(outcome.Result.CredentialId, outcome.Result.PublicKeyBase64);
    }

    // Register a new passkey + immediate-derive (Stage 3.a-1 contract).
    // Does NOT touch the disk manifest — that's EnterEncryptedAsync's job;
    // writing the manifest here would flip the VFS to Encrypted while on-
    // disk databases are still plaintext, stranding the user out of the
    // encryption plane.
    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var displayName = string.IsNullOrWhiteSpace(RegisterDisplayName) ? null : RegisterDisplayName.Trim();

        // Register is offered on a plain disk only, where the manifest carries no
        // hint and nothing gets excluded. The exclusion is the second lock on that
        // door: were a bound disk ever to reach here, a second passkey on the same
        // authenticator would derive a different PRF key and never unlock it, and
        // excluding the hint turns that dead end into CREDENTIAL_ALREADY_REGISTERED
        // at the ceremony rather than a passkey that silently cannot open the disk.
        var excludeCredentialIds = string.IsNullOrWhiteSpace(CredentialId)
            ? []
            : new[] { CredentialId };

        var result = await Authenticator.RegisterAsync(displayName, excludeCredentialIds, cancellationToken);

        if (!await ApplySessionAsync(result.CredentialId, result.PublicKeyBase64))
        {
            return;
        }

        RegisterDisplayName = null;
        StatusModel.AddSuccess(Localizer["Status_Registered"], nameof(Register));
    }

    // Drops the JS PRF cache + PublicKey, then re-reads the manifest: the
    // panel is about to become visible again and its shape (sign-in only vs.
    // sign-in + register) is the disk's to decide, not the previous
    // session's. Lock on an encrypted disk lands here.
    public async ValueTask ClearKeysAsync()
    {
        PrfService.ClearKeys();
        PublicKey = null;
        await RefreshPoolStateAsync();
    }

    // Full sign-out. CredentialId and PoolEncrypted come straight back from
    // the manifest, so an encrypted disk keeps its binding (sign-in targets
    // the bound credential) while a plain disk drops to the discoverable
    // picker with register alongside it.
    internal async ValueTask SignOutAsync()
    {
        PrfService.ClearKeys();
        PublicKey = null;
        RegisterDisplayName = null;
        WrongPasskeyPublicKey = null;
        WrongPasskeyCredentialId = null;
        await RefreshPoolStateAsync();
    }

    // Apply a freshly-derived session. Returns false when refused for a
    // disk-mismatch (wrong passkey for this disk's manifest hint) — caller
    // must skip post-success bookkeeping. Refusing here turns "wrong
    // passkey" into a clean status warning instead of an SQLITE_IOERR deep
    // in EF Core after the AuthorizeView flips Authorized under an unfit
    // VFS key. Does NOT write the manifest — that's EnterEncrypted's job.
    private async ValueTask<bool> ApplySessionAsync(string credentialId, string publicKeyBase64)
    {
        var poolState = await Session.GetStateAsync();
        PoolEncrypted = poolState.Encrypted;
        var poolHint = poolState.Hint;
        if (!string.IsNullOrEmpty(poolHint) &&
            !string.Equals(poolHint, credentialId, StringComparison.Ordinal))
        {
            PrfService.ClearKeys();
            // Expose the rejected pubkey for the user to copy — useful for
            // "export disk for recipient" against the other passkey.
            WrongPasskeyPublicKey = publicKeyBase64;
            WrongPasskeyCredentialId = credentialId;
            StatusModel.AddWarning(
                Localizer["Status_WrongPasskeyForPool"],
                nameof(SignIn));
            return false;
        }

        CredentialId = credentialId;
        PublicKey = publicKeyBase64;
        WrongPasskeyPublicKey = null;
        WrongPasskeyCredentialId = null;
        return true;
    }

    internal void DismissWrongPasskey()
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
        // The envelope only restores onto an encrypted disk, so the manifest
        // read the next sign-out does would say the same thing.
        PoolEncrypted = true;
        WrongPasskeyPublicKey = null;
        WrongPasskeyCredentialId = null;
    }

    // Trigger target for CredentialId/PublicKey — single sink that pushes
    // auth state through PrfAuthenticationStateProvider.
    private void PushAuthState()
    {
        StateProvider.UpdateAuthenticationState(CredentialId, PublicKey);
    }

    // No OperationCanceledException arm: RxBlazorV2 discards those as
    // switch-cancellation before the formatter runs, so an arm for one would be
    // dead code. PrfAuthenticator therefore reports an abandoned ceremony as
    // PrfErrorCode.CEREMONY_INCOMPLETE instead.
    private string FormatAuthenticateError(Exception ex) => ex switch
    {
        PrfAuthenticatorException { Operation: PrfAuthenticatorOperation.Authenticate, Code: var code, Detail: var detail } =>
            WithDetail(Localizer[$"Error_Authenticate_{code}"], detail),
        _ => Localizer["Error_Authenticate_Unknown", ex.Message],
    };

    private string FormatRegisterError(Exception ex) => ex switch
    {
        PrfAuthenticatorException { Operation: PrfAuthenticatorOperation.Register, Code: var code, Detail: var detail } =>
            WithDetail(Localizer[$"Error_Register_{code}"], detail),
        _ => Localizer["Error_Register_Unknown", ex.Message],
    };

    // The localized line says what happened; the browser's own diagnostic says
    // which variant of it — "PIN invalid" vs. a dismissed prompt both arrive as
    // NotAllowedError, so dropping the detail is what made the two look identical.
    private static string WithDetail(string message, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? message : $"{message} ({detail})";
}
