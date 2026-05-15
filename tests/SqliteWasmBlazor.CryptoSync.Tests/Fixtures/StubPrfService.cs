using R3;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

// Minimal IPrfService stub that exposes settable result lambdas for the
// methods PrfAuthenticator actually drives (IsPrfSupported / Register /
// DeriveKeys / DeriveKeysDiscoverable). Other members throw NotImplemented
// — PrfAuthenticator never touches them, so the throwing path is the
// contract assertion that the seam stays narrow.
//
// PrfAuthenticator.AuthenticateAsync(null) routes directly to
// DeriveKeysDiscoverableAsync (NOT DeriveKeysWithHintAsync) — the
// hint-aware path is reserved for the TestApp PrfVfsTest page that calls
// DeriveKeysWithHintAsync explicitly. The auth panel relies on the
// in-memory CredentialId mirror for hint-routing instead.
internal sealed class StubPrfService : IPrfService
{
    public Func<ValueTask<bool>> IsPrfSupported { get; set; } =
        () => ValueTask.FromResult(true);

    public Func<string?, ValueTask<PrfResult<PrfCredential>>> Register { get; set; } =
        _ => throw new InvalidOperationException("StubPrfService.Register not configured for this test.");

    public Func<string, ValueTask<PrfResult<string>>> DeriveKeys { get; set; } =
        _ => throw new InvalidOperationException("StubPrfService.DeriveKeys not configured for this test.");

    public Func<ValueTask<PrfResult<(string CredentialId, string PublicKey)>>> DeriveKeysDiscoverable { get; set; } =
        () => throw new InvalidOperationException("StubPrfService.DeriveKeysDiscoverable not configured for this test.");

    public List<string> RegisterDisplayNames { get; } = new();
    public List<string> DeriveKeysCredentialIds { get; } = new();
    public int DeriveKeysDiscoverableCalls { get; private set; }

    public KeyCacheStrategy CacheStrategy => KeyCacheStrategy.TIMED;

    public string Salt => "stub-salt";

    public Observable<string> KeyExpired { get; } = Observable.Empty<string>();

    public ValueTask<bool> IsPrfSupportedAsync() => IsPrfSupported();

    public ValueTask<PrfResult<PrfCredential>> RegisterAsync(string? displayName = null)
    {
        RegisterDisplayNames.Add(displayName ?? string.Empty);
        return Register(displayName);
    }

    public ValueTask<PrfResult<string>> DeriveKeysAsync(string credentialId)
    {
        DeriveKeysCredentialIds.Add(credentialId);
        return DeriveKeys(credentialId);
    }

    public ValueTask<PrfResult<(string CredentialId, string PublicKey)>> DeriveKeysDiscoverableAsync()
    {
        DeriveKeysDiscoverableCalls++;
        return DeriveKeysDiscoverable();
    }

    public string? GetCachedPublicKey() => throw new NotImplementedException();

    public bool HasCachedKeys() => throw new NotImplementedException();

    // Settable mirror for PrfBackedSenderAuthSigner / PrfBackedReceiveAuthSigner
    // — they only read GetEd25519PublicKey() to attach OwnEd25519PublicKeyBase64
    // to outgoing relay challenges.
    public string? Ed25519PublicKey { get; set; }

    public string? GetEd25519PublicKey() => Ed25519PublicKey;

    public void ClearKeys() => throw new NotImplementedException();

    public ValueTask<PrfResult<string>> DeriveDomainKeyAsync(string domainId, string context)
        => throw new NotImplementedException();

    public ValueTask<PrfResult<byte[]>> DecryptAsymmetricToBytesAsync(
        AsymmetricEncryptedData asymmetricEncrypted)
        => throw new NotImplementedException();
}
