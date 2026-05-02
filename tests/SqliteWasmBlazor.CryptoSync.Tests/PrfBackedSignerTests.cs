using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// R3.0 / Stage B coverage: PrfBackedSenderAuthSigner + PrfBackedReceiveAuthSigner
// against the same FakeKeyIdCryptoProvider that backs the SigningService
// keyId-cache tests. Drives a known synthetic PRF seed through:
//
//   seed → BouncyCastle.DeriveDualKeyPairAsync → cache under JsKeyId
//   → SigningService.SignAsync(msg, salt) → SignWithKeyIdAsync(msg, JsKeyId)
//   → SignSendChallengeAsync / SignReceiveChallengeAsync emits the sig
//
// Asserts: pubkey property routes to the public-key provider; sigs are
// deterministic per (seed, message); sigs verify under the matching pubkey
// and fail under a different pubkey; signer throws on missing PRF session
// and propagates upstream signing failures.
public class PrfBackedSignerTests
{
    private const string Salt = "device-1@example.com";
    private const string SendChallenge = "deltapost-v1|1714566600|abc123";
    private const string ReceiveChallenge = "1714566600|MGm0aW1jX9...";

    private static readonly byte[] PrfSeed = Enumerable.Range(0x40, 32)
        .Select(i => (byte)i)
        .ToArray();

    private static readonly byte[] OtherPrfSeed = Enumerable.Range(0x80, 32)
        .Select(i => (byte)i)
        .ToArray();

    // PrfKeyConventions is internal — replicate the keyId format so the
    // FakeKeyIdCryptoProvider lookup in SignWithKeyIdAsync agrees with what
    // SigningService routes through. SigningServiceKeyIdCacheTests pins the
    // same convention.
    private static string JsKeyId(string salt) => $"prf-keys:{salt}";

    private static (PrfBackedSenderAuthSigner SenderSigner,
                    PrfBackedReceiveAuthSigner ReceiveSigner,
                    FakeKeyIdCryptoProvider Provider,
                    StubEd25519PublicKeyProvider PublicKeyProvider)
        BuildSigners()
    {
        var provider = new FakeKeyIdCryptoProvider();
        var publicKeyProvider = new StubEd25519PublicKeyProvider();
        var signing = new SigningService(publicKeyProvider, provider);
        var prfOptions = Options.Create(new PrfOptions { Salt = Salt });
        var sender = new PrfBackedSenderAuthSigner(publicKeyProvider, signing, prfOptions);
        var receive = new PrfBackedReceiveAuthSigner(publicKeyProvider, signing, prfOptions);
        return (sender, receive, provider, publicKeyProvider);
    }

    private static async Task<string> SeedSessionAsync(
        FakeKeyIdCryptoProvider provider,
        StubEd25519PublicKeyProvider publicKeyProvider,
        byte[] seed)
    {
        var stored = await provider.StoreKeysAsync(JsKeyId(Salt), seed, ttlMs: null);
        Assert.True(stored.Success);
        Assert.NotNull(stored.Value);
        publicKeyProvider.SetEd25519PublicKey(stored.Value!.Ed25519PublicKey);
        return stored.Value.Ed25519PublicKey;
    }

    [Fact]
    public async Task SenderSigner_OwnPublicKey_MatchesSeededEd25519()
    {
        var (sender, _, provider, publicKeyProvider) = BuildSigners();
        var pubkey = await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        Assert.Equal(pubkey, sender.OwnEd25519PublicKeyBase64);
    }

    [Fact]
    public void SenderSigner_OwnPublicKey_ThrowsWhenNoSession()
    {
        var (sender, _, _, _) = BuildSigners();

        var ex = Assert.Throws<InvalidOperationException>(() => sender.OwnEd25519PublicKeyBase64);
        Assert.Contains("no PRF session", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiveSigner_OwnPublicKey_ThrowsWhenNoSession()
    {
        var (_, receive, _, _) = BuildSigners();

        var ex = Assert.Throws<InvalidOperationException>(() => receive.OwnEd25519PublicKeyBase64);
        Assert.Contains("no PRF session", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SenderSigner_SignSendChallenge_DeterministicForFixedSeedAndMessage()
    {
        var (sender, _, provider, publicKeyProvider) = BuildSigners();
        await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        var sigA = await sender.SignSendChallengeAsync(SendChallenge);
        var sigB = await sender.SignSendChallengeAsync(SendChallenge);

        Assert.Equal(sigA, sigB);
        Assert.False(string.IsNullOrEmpty(sigA));
    }

    [Fact]
    public async Task SenderSigner_SignSendChallenge_VerifiesUnderMatchingPubkey()
    {
        var (sender, _, provider, publicKeyProvider) = BuildSigners();
        var pubkey = await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        var sig = await sender.SignSendChallengeAsync(SendChallenge);

        // Verify via the same provider that did the signing — proves the
        // sig is real Ed25519 and routes through the keyId cache, not just
        // an opaque string passing through.
        var verified = await provider.VerifyAsync(SendChallenge, sig, pubkey);
        Assert.True(verified);
    }

    [Fact]
    public async Task SenderSigner_SignSendChallenge_FailsVerificationUnderDifferentPubkey()
    {
        var (sender, _, provider, publicKeyProvider) = BuildSigners();
        await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        var sig = await sender.SignSendChallengeAsync(SendChallenge);

        // Derive an unrelated Ed25519 pubkey from a different seed and
        // confirm the sig DOES NOT verify under it. Sanity check that the
        // earlier "verifies" assertion isn't a false positive.
        var unrelated = await provider.DeriveDualKeyPairAsync(OtherPrfSeed);
        var verified = await provider.VerifyAsync(SendChallenge, sig, unrelated.Ed25519PublicKey);
        Assert.False(verified);
    }

    [Fact]
    public async Task SenderSigner_SignSendChallenge_ThrowsWhenSessionEvicted()
    {
        var (sender, _, provider, publicKeyProvider) = BuildSigners();
        await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        // Simulate TTL expiry between OwnPublicKey access and Sign call —
        // SigningService.SignAsync surfaces a NOT_SUPPORTED / SIGNING_FAILED
        // PrfResult, which the signer wraps as InvalidOperationException so
        // HttpSyncTransport's async path gets a clear failure.
        provider.RemoveCachedKey(JsKeyId(Salt));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sender.SignSendChallengeAsync(SendChallenge));
    }

    [Fact]
    public async Task SenderSigner_SignSendChallenge_RespectsCancellation()
    {
        var (sender, _, _, _) = BuildSigners();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await sender.SignSendChallengeAsync(SendChallenge, cts.Token));
    }

    [Fact]
    public async Task ReceiveSigner_SignReceiveChallenge_DeterministicForFixedSeedAndMessage()
    {
        var (_, receive, provider, publicKeyProvider) = BuildSigners();
        await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        var sigA = await receive.SignReceiveChallengeAsync(ReceiveChallenge);
        var sigB = await receive.SignReceiveChallengeAsync(ReceiveChallenge);

        Assert.Equal(sigA, sigB);
    }

    [Fact]
    public async Task ReceiveSigner_SignReceiveChallenge_VerifiesUnderMatchingPubkey()
    {
        var (_, receive, provider, publicKeyProvider) = BuildSigners();
        var pubkey = await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        var sig = await receive.SignReceiveChallengeAsync(ReceiveChallenge);

        var verified = await provider.VerifyAsync(ReceiveChallenge, sig, pubkey);
        Assert.True(verified);
    }

    [Fact]
    public async Task SenderAndReceiveSigners_SameSeed_ProduceSameOwnPublicKey()
    {
        // Production wires both signers off the same PRF-derived Ed25519
        // keypair — confirm the seam holds when both are seeded once.
        var (sender, receive, provider, publicKeyProvider) = BuildSigners();
        await SeedSessionAsync(provider, publicKeyProvider, PrfSeed);

        Assert.Equal(sender.OwnEd25519PublicKeyBase64, receive.OwnEd25519PublicKeyBase64);
    }
}
