using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Production <see cref="IReceiveAuthSigner"/> backed by the PRF-derived
/// Ed25519 keypair held in the JS-side non-extractable key cache. Signs
/// the receive challenge <c>"{timestamp}|{ownPubKey}"</c> on every
/// <c>GET /api/delta</c> so the relay can prove the requester owns the
/// inbox they're trying to drain (metadata-leak prevention — confidentiality
/// of the inbox contents is already provided by the V2 envelope).
///
/// <para>
/// Mechanics mirror <see cref="PrfBackedSenderAuthSigner"/>: routes via
/// <see cref="ISigningService.SignAsync"/> →
/// <c>ICryptoProvider.SignWithKeyIdAsync</c>, priv stays JS-side as a
/// non-extractable <c>SubtleCrypto</c> key. Production typically wires
/// the same Ed25519 keypair to both signers; the seam stays separate so
/// tests and exotic deployments can swap independently.
/// </para>
/// </summary>
public sealed class PrfBackedReceiveAuthSigner : IReceiveAuthSigner
{
    private readonly IEd25519PublicKeyProvider _publicKeyProvider;
    private readonly ISigningService _signing;
    private readonly PrfOptions _prfOptions;

    public PrfBackedReceiveAuthSigner(
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
               "PrfBackedReceiveAuthSigner: no PRF session — call DeriveKeysWithHintAsync (or equivalent) before signing.");

    public async ValueTask<string> SignReceiveChallengeAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _signing.SignAsync(message, _prfOptions.Salt);
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException(
                $"PrfBackedReceiveAuthSigner.SignReceiveChallenge failed: {result.ErrorCode}");
        }
        return result.Value;
    }
}
