using R3;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

// Minimal IPrfService stub that exposes settable result lambdas for the
// methods PrfAuthenticator actually drives (IsPrfSupported / Register /
// DeriveKeys / DeriveKeysWithHint). Other members throw NotImplemented —
// PrfAuthenticator never touches them, so the throwing path is the
// contract assertion that the seam stays narrow.
internal sealed class StubPrfService : IPrfService
{
    public Func<ValueTask<bool>> IsPrfSupported { get; set; } =
        () => ValueTask.FromResult(true);

    public Func<string?, ValueTask<PrfResult<PrfCredential>>> Register { get; set; } =
        _ => throw new InvalidOperationException("StubPrfService.Register not configured for this test.");

    public Func<string, ValueTask<PrfResult<string>>> DeriveKeys { get; set; } =
        _ => throw new InvalidOperationException("StubPrfService.DeriveKeys not configured for this test.");

    public Func<ValueTask<PrfResult<(string CredentialId, string PublicKey)>>> DeriveKeysWithHint { get; set; } =
        () => throw new InvalidOperationException("StubPrfService.DeriveKeysWithHint not configured for this test.");

    public List<string> RegisterDisplayNames { get; } = new();
    public List<string> DeriveKeysCredentialIds { get; } = new();
    public int DeriveKeysWithHintCalls { get; private set; }

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
        => throw new NotImplementedException("PrfAuthenticator must not call DeriveKeysDiscoverableAsync directly.");

    public ValueTask<PrfResult<(string CredentialId, string PublicKey)>> DeriveKeysWithHintAsync()
    {
        DeriveKeysWithHintCalls++;
        return DeriveKeysWithHint();
    }

    public string? GetCachedPublicKey() => throw new NotImplementedException();

    public bool HasCachedKeys() => throw new NotImplementedException();

    public string? GetEd25519PublicKey() => throw new NotImplementedException();

    public void ClearKeys() => throw new NotImplementedException();

    public ValueTask<PrfResult<string>> DeriveDomainKeyAsync(string domainId, string context)
        => throw new NotImplementedException();
}
