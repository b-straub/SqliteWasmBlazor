using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Production <see cref="IPrfAuthenticator"/> implementation that bridges the
/// host-supplied seam consumed by <see cref="Components.Authentication.AuthenticationPanel"/>
/// onto the base-plane <see cref="IPrfService"/>. No new TS surface — the underlying
/// WebAuthn-PRF pipeline (<c>crypto-bridge.ts</c>, <c>navigator.credentials.create/get</c>,
/// X25519 derivation, <c>SecureKeyCache</c>) is already production-grade and
/// exercised end-to-end by the R2 / R3 Playwright suites.
///
/// <para>
/// <b>Register-then-derive contract.</b> WebAuthn create + assert are two
/// ceremonies. <see cref="RegisterAsync"/> runs both back-to-back so it can
/// satisfy the seam contract of returning the X25519 pubkey alongside the
/// credential id. The user sees two platform prompts — accepted UX for a
/// "create my passkey" gesture.
/// </para>
///
/// <para>
/// <b>Failure surfacing.</b> Per the seam: register-time user cancel throws
/// <see cref="OperationCanceledException"/>; authenticate-time user cancel
/// returns <c>null</c>; transport / WebAuthn errors throw
/// <see cref="PrfAuthenticatorException"/> with the structured
/// <see cref="PrfErrorCode"/> intact so the panel formatters can localize via
/// per-code resx keys (<c>Error_Register_{code}</c> /
/// <c>Error_Authenticate_{code}</c>) rather than embedding a hardcoded
/// English string from <see cref="PrfErrorMessages.GetMessage"/>.
/// </para>
/// </summary>
internal sealed class PrfAuthenticator : IPrfAuthenticator
{
    private readonly IPrfService _prf;

    public PrfAuthenticator(IPrfService prf)
    {
        _prf = prf;
    }

    public ValueTask<bool> CheckPrfSupportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _prf.IsPrfSupportedAsync();
    }

    public async ValueTask<PrfRegistrationResult> RegisterAsync(
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var registerResult = await _prf.RegisterAsync(displayName);
        if (registerResult.Cancelled)
        {
            throw new OperationCanceledException(
                "User cancelled passkey registration.",
                cancellationToken);
        }
        if (!registerResult.Success || registerResult.Value is null)
        {
            throw new PrfAuthenticatorException(
                PrfAuthenticatorOperation.Register,
                registerResult.ErrorCode ?? PrfErrorCode.REGISTRATION_FAILED);
        }

        var credential = registerResult.Value;
        cancellationToken.ThrowIfCancellationRequested();

        var deriveResult = await _prf.DeriveKeysAsync(credential.RawId);
        if (deriveResult.Cancelled)
        {
            throw new OperationCanceledException(
                "User cancelled the post-registration key derivation ceremony.",
                cancellationToken);
        }
        if (!deriveResult.Success || deriveResult.Value is null)
        {
            throw new PrfAuthenticatorException(
                PrfAuthenticatorOperation.Register,
                deriveResult.ErrorCode ?? PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        return new PrfRegistrationResult(credential.RawId, deriveResult.Value);
    }

    public async ValueTask<PrfAuthenticationResult?> AuthenticateAsync(
        string? credentialIdHint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(credentialIdHint))
        {
            var byHint = await _prf.DeriveKeysAsync(credentialIdHint);
            if (byHint.Cancelled)
            {
                return null;
            }
            if (!byHint.Success || byHint.Value is null)
            {
                throw new PrfAuthenticatorException(
                    PrfAuthenticatorOperation.Authenticate,
                    byHint.ErrorCode ?? PrfErrorCode.KEY_DERIVATION_FAILED);
            }
            return new PrfAuthenticationResult(credentialIdHint, byHint.Value);
        }

        // Caller asked for discoverable explicitly — go straight to the
        // platform picker. DeriveKeysWithHintAsync would re-read the
        // persisted hint and run a targeted ceremony first, which is
        // wrong here: the panel only routes through this branch after
        // the hinted prompt has already been cancelled (or there is no
        // hint at all). Stacking another hinted ceremony in front of
        // the discoverable picker is exactly the redundant-prompts
        // chain the UI is meant to avoid.
        var byDiscoverable = await _prf.DeriveKeysDiscoverableAsync();
        if (byDiscoverable.Cancelled)
        {
            return null;
        }
        if (!byDiscoverable.Success)
        {
            throw new PrfAuthenticatorException(
                PrfAuthenticatorOperation.Authenticate,
                byDiscoverable.ErrorCode ?? PrfErrorCode.KEY_DERIVATION_FAILED);
        }
        // PrfResult<T>.Value for an unconstrained T resolves to T itself (not
        // Nullable<T>) when T is a value type — the value-tuple components
        // here are populated under the IPrfService.Success contract.
        var (credentialId, publicKey) = byDiscoverable.Value;
        return new PrfAuthenticationResult(credentialId, publicKey);
    }
}
