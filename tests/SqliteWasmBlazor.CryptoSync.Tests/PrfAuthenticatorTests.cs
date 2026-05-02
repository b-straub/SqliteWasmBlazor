using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.UI.Services;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Stage 1 coverage of the production IPrfAuthenticator implementation.
// PrfAuthenticator is a thin C# adapter over IPrfService — no new TS
// surface, no WebAuthn ceremony driven from xUnit. Tests pin the four
// behaviors the seam contract defines:
//
//   1. CheckPrfSupportAsync forwards.
//   2. RegisterAsync runs both ceremonies (create + derive) back-to-back
//      and returns the credential id + X25519 pubkey from the second
//      ceremony.
//   3. AuthenticateAsync routes hint → DeriveKeysAsync; no-hint →
//      DeriveKeysWithHintAsync; cancellation → null; error → throw.
//   4. RegisterAsync surfaces user-cancel as OperationCanceledException
//      (per the seam contract; AuthenticationModel handles it through
//      the per-command formatter; RegistrationModel has no null-cancel
//      path).
//
// Uses StubPrfService to drive deterministic outcomes — the
// FakeKeyIdCryptoProvider harness from R1.3 / R1.4 is overkill here
// because PrfAuthenticator never composes signing or AEAD primitives.
public class PrfAuthenticatorTests
{
    private const string CredentialId = "cred-raw-id-base64-stub";
    private const string PublicKey = "x25519-pubkey-base64-stub";
    private const string OtherCredentialId = "other-cred-raw-id";
    private const string OtherPublicKey = "other-pubkey-base64";

    [Fact]
    public async Task CheckPrfSupportAsync_ForwardsToPrfService()
    {
        var stub = new StubPrfService { IsPrfSupported = () => ValueTask.FromResult(true) };
        var auth = new PrfAuthenticator(stub);

        Assert.True(await auth.CheckPrfSupportAsync());

        stub.IsPrfSupported = () => ValueTask.FromResult(false);
        Assert.False(await auth.CheckPrfSupportAsync());
    }

    [Fact]
    public async Task RegisterAsync_RunsCreateThenDerive_ReturnsBothPieces()
    {
        var stub = new StubPrfService
        {
            Register = _ => ValueTask.FromResult(
                PrfResult<PrfCredential>.Ok(new PrfCredential(Id: "url-safe-id", RawId: CredentialId))),
            DeriveKeys = _ => ValueTask.FromResult(PrfResult<string>.Ok(PublicKey)),
        };
        var auth = new PrfAuthenticator(stub);

        var result = await auth.RegisterAsync(displayName: "Demo passkey");

        Assert.Equal(CredentialId, result.CredentialId);
        Assert.Equal(PublicKey, result.PublicKeyBase64);
        Assert.Single(stub.RegisterDisplayNames, "Demo passkey");
        Assert.Single(stub.DeriveKeysCredentialIds, CredentialId); // RawId, not Id — matches PrfService.RegisterAsync hint persistence
    }

    [Fact]
    public async Task RegisterAsync_UserCancelOnCreate_ThrowsOperationCanceled()
    {
        var stub = new StubPrfService
        {
            Register = _ => ValueTask.FromResult(PrfResult<PrfCredential>.UserCancelled()),
            // DeriveKeys must NOT be called — left at the throwing default.
        };
        var auth = new PrfAuthenticator(stub);

        await Assert.ThrowsAsync<OperationCanceledException>(() => auth.RegisterAsync(null).AsTask());
        Assert.Empty(stub.DeriveKeysCredentialIds);
    }

    [Fact]
    public async Task RegisterAsync_UserCancelOnDerive_ThrowsOperationCanceled()
    {
        var stub = new StubPrfService
        {
            Register = _ => ValueTask.FromResult(
                PrfResult<PrfCredential>.Ok(new PrfCredential(Id: "url-safe-id", RawId: CredentialId))),
            DeriveKeys = _ => ValueTask.FromResult(PrfResult<string>.UserCancelled()),
        };
        var auth = new PrfAuthenticator(stub);

        await Assert.ThrowsAsync<OperationCanceledException>(() => auth.RegisterAsync(null).AsTask());
    }

    [Fact]
    public async Task RegisterAsync_CreateError_ThrowsStructuredException()
    {
        var stub = new StubPrfService
        {
            Register = _ => ValueTask.FromResult(PrfResult<PrfCredential>.Fail(PrfErrorCode.REGISTRATION_FAILED)),
        };
        var auth = new PrfAuthenticator(stub);

        var ex = await Assert.ThrowsAsync<PrfAuthenticatorException>(
            () => auth.RegisterAsync(null).AsTask());
        Assert.Equal(PrfAuthenticatorOperation.Register, ex.Operation);
        Assert.Equal(PrfErrorCode.REGISTRATION_FAILED, ex.Code);
    }

    [Fact]
    public async Task RegisterAsync_DeriveError_ThrowsStructuredException()
    {
        var stub = new StubPrfService
        {
            Register = _ => ValueTask.FromResult(
                PrfResult<PrfCredential>.Ok(new PrfCredential(Id: "url-safe-id", RawId: CredentialId))),
            DeriveKeys = _ => ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED)),
        };
        var auth = new PrfAuthenticator(stub);

        var ex = await Assert.ThrowsAsync<PrfAuthenticatorException>(
            () => auth.RegisterAsync(null).AsTask());
        Assert.Equal(PrfAuthenticatorOperation.Register, ex.Operation);
        Assert.Equal(PrfErrorCode.KEY_DERIVATION_FAILED, ex.Code);
    }

    [Fact]
    public async Task AuthenticateAsync_WithHint_RoutesThroughDeriveKeysAsync()
    {
        var stub = new StubPrfService
        {
            DeriveKeys = _ => ValueTask.FromResult(PrfResult<string>.Ok(PublicKey)),
        };
        var auth = new PrfAuthenticator(stub);

        var result = await auth.AuthenticateAsync(credentialIdHint: CredentialId);

        Assert.NotNull(result);
        Assert.Equal(CredentialId, result.CredentialId);
        Assert.Equal(PublicKey, result.PublicKeyBase64);
        Assert.Single(stub.DeriveKeysCredentialIds, CredentialId);
        Assert.Equal(0, stub.DeriveKeysWithHintCalls);
    }

    [Fact]
    public async Task AuthenticateAsync_NoHint_RoutesThroughDeriveKeysWithHintAsync()
    {
        var stub = new StubPrfService
        {
            DeriveKeysWithHint = () => ValueTask.FromResult(
                PrfResult<(string CredentialId, string PublicKey)>.Ok((OtherCredentialId, OtherPublicKey))),
        };
        var auth = new PrfAuthenticator(stub);

        var result = await auth.AuthenticateAsync(credentialIdHint: null);

        Assert.NotNull(result);
        Assert.Equal(OtherCredentialId, result.CredentialId);
        Assert.Equal(OtherPublicKey, result.PublicKeyBase64);
        Assert.Equal(1, stub.DeriveKeysWithHintCalls);
        Assert.Empty(stub.DeriveKeysCredentialIds);
    }

    [Fact]
    public async Task AuthenticateAsync_WhitespaceHint_FallsThroughToDiscoverable()
    {
        var stub = new StubPrfService
        {
            DeriveKeysWithHint = () => ValueTask.FromResult(
                PrfResult<(string CredentialId, string PublicKey)>.Ok((OtherCredentialId, OtherPublicKey))),
        };
        var auth = new PrfAuthenticator(stub);

        var result = await auth.AuthenticateAsync(credentialIdHint: "   ");

        Assert.NotNull(result);
        Assert.Equal(OtherCredentialId, result.CredentialId);
        Assert.Equal(1, stub.DeriveKeysWithHintCalls);
    }

    [Fact]
    public async Task AuthenticateAsync_HintedUserCancel_ReturnsNull()
    {
        var stub = new StubPrfService
        {
            DeriveKeys = _ => ValueTask.FromResult(PrfResult<string>.UserCancelled()),
        };
        var auth = new PrfAuthenticator(stub);

        var result = await auth.AuthenticateAsync(credentialIdHint: CredentialId);

        Assert.Null(result);
    }

    [Fact]
    public async Task AuthenticateAsync_DiscoverableUserCancel_ReturnsNull()
    {
        var stub = new StubPrfService
        {
            DeriveKeysWithHint = () => ValueTask.FromResult(
                PrfResult<(string CredentialId, string PublicKey)>.UserCancelled()),
        };
        var auth = new PrfAuthenticator(stub);

        var result = await auth.AuthenticateAsync(credentialIdHint: null);

        Assert.Null(result);
    }

    [Fact]
    public async Task AuthenticateAsync_HintedError_ThrowsStructuredException()
    {
        var stub = new StubPrfService
        {
            DeriveKeys = _ => ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH)),
        };
        var auth = new PrfAuthenticator(stub);

        var ex = await Assert.ThrowsAsync<PrfAuthenticatorException>(
            () => auth.AuthenticateAsync(credentialIdHint: CredentialId).AsTask());
        Assert.Equal(PrfAuthenticatorOperation.Authenticate, ex.Operation);
        Assert.Equal(PrfErrorCode.AUTHENTICATION_TAG_MISMATCH, ex.Code);
    }

    [Fact]
    public async Task AuthenticateAsync_DiscoverableError_ThrowsStructuredException()
    {
        var stub = new StubPrfService
        {
            DeriveKeysWithHint = () => ValueTask.FromResult(
                PrfResult<(string CredentialId, string PublicKey)>.Fail(PrfErrorCode.CREDENTIAL_NOT_FOUND)),
        };
        var auth = new PrfAuthenticator(stub);

        var ex = await Assert.ThrowsAsync<PrfAuthenticatorException>(
            () => auth.AuthenticateAsync(credentialIdHint: null).AsTask());
        Assert.Equal(PrfAuthenticatorOperation.Authenticate, ex.Operation);
        Assert.Equal(PrfErrorCode.CREDENTIAL_NOT_FOUND, ex.Code);
    }

    // The structured exception's Operation field is what the panel
    // formatter switches on (RegistrationModel only handles Register,
    // AuthenticationModel only handles Authenticate). Pin that the
    // PrfAuthenticator never crosses-wires the two so a Register-flow
    // failure can't get rendered with an Authenticate-flow resx key.
    [Fact]
    public async Task RegisterAsync_FailureCarriesRegisterOperation()
    {
        var stub = new StubPrfService
        {
            Register = _ => ValueTask.FromResult(PrfResult<PrfCredential>.Fail(PrfErrorCode.PRF_NOT_SUPPORTED)),
        };
        var auth = new PrfAuthenticator(stub);

        var ex = await Assert.ThrowsAsync<PrfAuthenticatorException>(
            () => auth.RegisterAsync(null).AsTask());
        Assert.Equal(PrfAuthenticatorOperation.Register, ex.Operation);
    }

    [Fact]
    public async Task AuthenticateAsync_FailureCarriesAuthenticateOperation()
    {
        var stub = new StubPrfService
        {
            DeriveKeysWithHint = () => ValueTask.FromResult(
                PrfResult<(string CredentialId, string PublicKey)>.Fail(PrfErrorCode.PRF_NOT_SUPPORTED)),
        };
        var auth = new PrfAuthenticator(stub);

        var ex = await Assert.ThrowsAsync<PrfAuthenticatorException>(
            () => auth.AuthenticateAsync(credentialIdHint: null).AsTask());
        Assert.Equal(PrfAuthenticatorOperation.Authenticate, ex.Operation);
    }

    [Fact]
    public async Task RegisterAsync_PreCancelled_ThrowsBeforeContacting_PrfService()
    {
        var stub = new StubPrfService(); // both lambdas left at throwing defaults
        var auth = new PrfAuthenticator(stub);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => auth.RegisterAsync(displayName: null, cts.Token).AsTask());
        Assert.Empty(stub.RegisterDisplayNames);
        Assert.Empty(stub.DeriveKeysCredentialIds);
    }
}
