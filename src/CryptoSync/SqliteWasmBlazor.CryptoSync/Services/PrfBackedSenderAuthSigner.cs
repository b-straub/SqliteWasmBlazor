using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Production <see cref="ISenderAuthSigner"/> backed by the PRF-derived
/// Ed25519 keypair held in the JS-side non-extractable key cache. Signing
/// goes through <see cref="ISigningService.SignAsync"/> which routes via
/// <c>ICryptoProvider.SignWithKeyIdAsync</c> using the salt-derived
/// <c>PrfKeyConventions.GetJsKeyId</c> — the priv never crosses the
/// C#↔JS boundary, so there is no managed buffer to zero on this side.
///
/// <para>
/// Caller responsibility: a PRF session must be active before either
/// member is touched. <see cref="OwnEd25519PublicKeyBase64"/> throws
/// when no session is cached, so a sender that gets to the point of
/// signing without first calling <c>PrfService.DeriveKeysWithHintAsync</c>
/// (or equivalent) gets a clear failure rather than a silent stub sig.
/// </para>
/// </summary>
public sealed class PrfBackedSenderAuthSigner : ISenderAuthSigner
{
    private readonly IEd25519PublicKeyProvider _publicKeyProvider;
    private readonly ISigningService _signing;
    private readonly PrfOptions _prfOptions;

    public PrfBackedSenderAuthSigner(
        IEd25519PublicKeyProvider publicKeyProvider,
        ISigningService signing,
        IOptions<PrfOptions> prfOptions)
    {
        _publicKeyProvider = publicKeyProvider;
        _signing = signing;
        _prfOptions = prfOptions.Value;
    }

    public string OwnEd25519PublicKeyBase64
        => _publicKeyProvider.GetEd25519PublicKey()
           ?? throw new InvalidOperationException(
               "PrfBackedSenderAuthSigner: no PRF session — call DeriveKeysWithHintAsync (or equivalent) before signing.");

    public async ValueTask<string> SignSendChallengeAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _signing.SignAsync(message, _prfOptions.Salt);
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException(
                $"PrfBackedSenderAuthSigner.SignSendChallenge failed: {result.ErrorCode}");
        }
        return result.Value;
    }
}
