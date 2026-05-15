using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Production <see cref="ISenderAuthSigner"/> backed by the PRF-derived
/// Ed25519 keypair held in the JS-side non-extractable key cache. Signing
/// goes through <c>ICryptoProvider.SignWithKeyIdAsync</c> with the
/// salt-derived <c>PrfKeyConventions.GetJsKeyId</c> — the priv never
/// crosses the C#↔JS boundary, so there is no managed buffer to zero
/// on this side.
///
/// <para>
/// Caller responsibility: a PRF session must be active before either
/// member is touched. <see cref="OwnEd25519PublicKeyBase64"/> throws
/// when no session is cached, so a sender that gets to the point of
/// signing without first calling <c>PrfService.DeriveKeysWithHintAsync</c>
/// (or equivalent) gets a clear failure rather than a silent stub sig.
/// </para>
/// </summary>
internal sealed class PrfBackedSenderAuthSigner : ISenderAuthSigner
{
    private readonly IPrfService _prfService;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly PrfOptions _prfOptions;

    public PrfBackedSenderAuthSigner(
        IPrfService prfService,
        ICryptoProvider cryptoProvider,
        IOptions<PrfOptions> prfOptions)
    {
        _prfService = prfService;
        _cryptoProvider = cryptoProvider;
        _prfOptions = prfOptions.Value;
    }

    public string OwnEd25519PublicKeyBase64
        => _prfService.GetEd25519PublicKey()
           ?? throw new InvalidOperationException(
               "PrfBackedSenderAuthSigner: no PRF session — call DeriveKeysWithHintAsync (or equivalent) before signing.");

    public async ValueTask<string> SignSendChallengeAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _cryptoProvider.SignWithKeyIdAsync(
            message, PrfKeyConventions.GetJsKeyId(_prfOptions.Salt));
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException(
                $"PrfBackedSenderAuthSigner.SignSendChallenge failed: {result.ErrorCode}");
        }
        return result.Value;
    }
}
