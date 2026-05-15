using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Production <see cref="IReceiveAuthSigner"/> backed by the PRF-derived
/// Ed25519 keypair held in the JS-side non-extractable key cache. Signs
/// the receive challenge <c>"{timestamp}|{ownPubKey}"</c> on every
/// <c>GET /api/delta</c> so the relay can prove the requester owns the
/// inbox they're trying to drain (metadata-leak prevention — confidentiality
/// of the inbox contents is already provided by the delta envelope).
///
/// <para>
/// Mechanics mirror <see cref="PrfBackedSenderAuthSigner"/>: routes through
/// <c>ICryptoProvider.SignWithKeyIdAsync</c> with the salt-derived
/// <c>PrfKeyConventions.GetJsKeyId</c>, priv stays JS-side as a
/// non-extractable <c>SubtleCrypto</c> key. Production typically wires
/// the same Ed25519 keypair to both signers; the seam stays separate so
/// tests and exotic deployments can swap independently.
/// </para>
/// </summary>
internal sealed class PrfBackedReceiveAuthSigner : IReceiveAuthSigner
{
    private readonly IPrfService _prfService;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly PrfOptions _prfOptions;

    public PrfBackedReceiveAuthSigner(
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
               "PrfBackedReceiveAuthSigner: no PRF session — call DeriveKeysWithHintAsync (or equivalent) before signing.");

    public async ValueTask<string> SignReceiveChallengeAsync(
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
                $"PrfBackedReceiveAuthSigner.SignReceiveChallenge failed: {result.ErrorCode}");
        }
        return result.Value;
    }
}
