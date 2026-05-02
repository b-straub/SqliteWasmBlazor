using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Which PrfAuthenticator operation failed. Lets the per-command error
/// formatter (<see cref="Components.Authentication.RegistrationModel.FormatRegisterError"/>
/// / <see cref="Components.Authentication.AuthenticationModel.FormatDeriveKeysError"/>)
/// pick the right localized resx prefix without re-deriving from the
/// stack.
/// </summary>
public enum PrfAuthenticatorOperation
{
    Register,
    Authenticate,
}

/// <summary>
/// Carries a structured <see cref="PrfErrorCode"/> failure out of
/// <see cref="PrfAuthenticator"/> so the panel formatters can localize
/// the user-visible message via per-code resx keys
/// (<c>Error_{Operation}_{Code}</c>) instead of embedding a hardcoded
/// English string from <see cref="PrfErrorMessages.GetMessage"/>.
///
/// <para>
/// The base <see cref="Exception.Message"/> stays in English with code
/// + canonical message — useful for logs / devtools, never user-facing.
/// User-visible text is always resolved through the
/// <see cref="Microsoft.Extensions.Localization.IStringLocalizer"/>
/// in the consuming model.
/// </para>
///
/// <para>
/// User-cancellation is intentionally NOT routed through this exception:
/// the seam contract surfaces it as <see cref="OperationCanceledException"/>
/// (RegisterAsync) or a <c>null</c> return (AuthenticateAsync), per
/// <see cref="IPrfAuthenticator"/>.
/// </para>
/// </summary>
public sealed class PrfAuthenticatorException : Exception
{
    public PrfErrorCode Code { get; }
    public PrfAuthenticatorOperation Operation { get; }

    public PrfAuthenticatorException(
        PrfAuthenticatorOperation operation,
        PrfErrorCode code)
        : base($"PrfAuthenticator.{operation} failed: {code} — {PrfErrorMessages.GetMessage(code)}")
    {
        Operation = operation;
        Code = code;
    }
}
