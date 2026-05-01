using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Round-trip test for SigningService against the keyId-cache contract.
// Covers the post-2026-04-30 secret-boundary refactor where SigningService
// stopped carrying Ed25519 private key bytes across the C#↔JS boundary
// and started routing through ICryptoProvider.SignWithKeyIdAsync; the
// JS side keeps the private key as a non-extractable SubtleCrypto key
// looked up by keyId.
//
// FakeKeyIdCryptoProvider stands in for the JS-side cache: an in-memory
// dictionary holds the DualKeyPairFull derived from a stored PRF seed,
// and SignWithKeyIdAsync looks up the Ed25519 private key by keyId.
// BouncyCastle does the actual signing/verifying.
public class SigningServiceKeyIdCacheTests
{
    private const string Salt = "user@example.com";
    private static readonly byte[] PrfSeed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly FakeKeyIdCryptoProvider _provider = new();
    private readonly SigningService _signingService;
    private readonly StubEd25519PublicKeyProvider _publicKeyProvider = new();

    public SigningServiceKeyIdCacheTests()
    {
        _signingService = new SigningService(_publicKeyProvider, _provider);
    }

    private async Task<string> SeedCacheAsync()
    {
        var keyId = PrfKeyConventions_GetJsKeyIdViaSign(Salt);
        var stored = await _provider.StoreKeysAsync(keyId, PrfSeed, ttlMs: null);
        Assert.True(stored.Success);
        Assert.NotNull(stored.Value);
        _publicKeyProvider.SetEd25519PublicKey(stored.Value!.Ed25519PublicKey);
        return keyId;
    }

    // PrfKeyConventions is internal — replicate the keyId format here so
    // the test stays a single namespace away from the contract under test.
    // If this format ever changes, both this string and the production
    // PrfKeyConventions need updating; the [Fact] below pins agreement.
    private static string PrfKeyConventions_GetJsKeyIdViaSign(string salt) => $"prf-keys:{salt}";

    [Fact]
    public async Task SignAsync_RoutesThroughKeyIdCache_ProducesVerifiableSignature()
    {
        await SeedCacheAsync();

        var sig = await _signingService.SignAsync("hello", Salt);
        Assert.True(sig.Success);
        Assert.NotNull(sig.Value);

        var verified = await _signingService.VerifyAsync("hello", sig.Value!, _publicKeyProvider.GetEd25519PublicKey()!);
        Assert.True(verified);
    }

    [Fact]
    public async Task SignAsync_DifferentMessage_ProducesDifferentSignature()
    {
        await SeedCacheAsync();

        var sigA = await _signingService.SignAsync("message-A", Salt);
        var sigB = await _signingService.SignAsync("message-B", Salt);
        Assert.True(sigA.Success);
        Assert.True(sigB.Success);

        Assert.NotEqual(sigA.Value, sigB.Value);
    }

    [Fact]
    public async Task SignAsync_AfterRemoveCachedKey_Fails()
    {
        var keyId = await SeedCacheAsync();
        Assert.True(_provider.HasCachedKey(keyId));

        _provider.RemoveCachedKey(keyId);
        Assert.False(_provider.HasCachedKey(keyId));

        var sig = await _signingService.SignAsync("after-evict", Salt);
        Assert.False(sig.Success);
    }

    [Fact]
    public async Task CreateSignedMessageAsync_RoundTrip_VerifiesAndIncludesTimestamp()
    {
        await SeedCacheAsync();

        var signed = await _signingService.CreateSignedMessageAsync("payload-1", Salt);
        Assert.True(signed.Success);
        Assert.NotNull(signed.Value);
        Assert.Equal("payload-1", signed.Value!.Message);
        Assert.Equal(_publicKeyProvider.GetEd25519PublicKey(), signed.Value.PublicKey);
        Assert.True(signed.Value.TimestampUnix > 0);

        var ok = await _signingService.VerifySignedMessageAsync(signed.Value);
        Assert.True(ok);
    }

    [Fact]
    public async Task SignAsync_KeyIdConvention_MatchesProductionFormat()
    {
        // Pin the keyId format SigningService routes through. Production
        // PrfKeyConventions.GetJsKeyId is internal; this test asserts that
        // a keyId stored under our test format is in fact what
        // SigningService looks up via SignWithKeyIdAsync.
        var expectedKeyId = PrfKeyConventions_GetJsKeyIdViaSign(Salt);
        var stored = await _provider.StoreKeysAsync(expectedKeyId, PrfSeed, ttlMs: null);
        Assert.True(stored.Success);
        _publicKeyProvider.SetEd25519PublicKey(stored.Value!.Ed25519PublicKey);

        var sig = await _signingService.SignAsync("conv", Salt);
        Assert.True(sig.Success);

        // If SigningService used a different keyId convention, the cache
        // lookup in FakeKeyIdCryptoProvider.SignWithKeyIdAsync would miss
        // and the sign call would fail.
        Assert.True(_provider.HasCachedKey(expectedKeyId));
    }

    [Fact]
    public async Task SignAsync_DeterministicForSamePrfSeed_AcrossCacheRefills()
    {
        // Ed25519 signatures are deterministic per (private key, message),
        // so signing the same message after evicting and re-storing the
        // same cached keys should produce the identical signature.
        var keyId = await SeedCacheAsync();
        var sigBefore = await _signingService.SignAsync("repeatable", Salt);

        _provider.RemoveCachedKey(keyId);
        await _provider.StoreKeysAsync(keyId, PrfSeed, ttlMs: null);

        var sigAfter = await _signingService.SignAsync("repeatable", Salt);

        Assert.Equal(sigBefore.Value, sigAfter.Value);
    }
}

internal sealed class StubEd25519PublicKeyProvider : IEd25519PublicKeyProvider
{
    private string? _publicKey;
    public void SetEd25519PublicKey(string publicKey) => _publicKey = publicKey;
    public string? GetEd25519PublicKey() => _publicKey;
}
