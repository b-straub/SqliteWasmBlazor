using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Round-trip test for AsymmetricEncryptionService against the keyId-cache
// contract. Mirrors SigningServiceKeyIdCacheTests on the X25519 side.
//
// AsymmetricEncryptionService.DecryptAsync routes through
// ICryptoProvider.DecryptAsymmetricWithKeyIdAsync; the JS side keeps the
// X25519 private key as a non-extractable SubtleCrypto key and looks it
// up by keyId. FakeKeyIdCryptoProvider stands in for that cache: an
// in-memory dictionary holds the DualKeyPairFull, and decryption looks
// up the X25519 private key by keyId then delegates to BouncyCastle.
public class AsymmetricEncryptionServiceKeyIdCacheTests
{
    private const string Salt = "alice@example.com";
    private static readonly byte[] PrfSeed = Enumerable.Repeat((byte)0x55, 32).ToArray();

    private readonly FakeKeyIdCryptoProvider _provider = new();
    private readonly AsymmetricEncryptionService _service;
    private readonly StubEd25519PublicKeyProvider _publicKeyProvider = new();
    private readonly SigningService _signingService;

    public AsymmetricEncryptionServiceKeyIdCacheTests()
    {
        _service = new AsymmetricEncryptionService(_provider);
        _signingService = new SigningService(_publicKeyProvider, _provider);
    }

    private async Task<(string KeyId, string X25519Pub)> SeedCacheAsync()
    {
        var keyId = $"prf-keys:{Salt}";
        var stored = await _provider.StoreKeysAsync(keyId, PrfSeed, ttlMs: null);
        Assert.True(stored.Success);
        Assert.NotNull(stored.Value);
        _publicKeyProvider.SetEd25519PublicKey(stored.Value!.Ed25519PublicKey);
        return (keyId, stored.Value!.X25519PublicKey);
    }

    [Fact]
    public async Task EncryptThenDecrypt_RoutesThroughKeyIdCache_RoundTrips()
    {
        var (_, recipientPub) = await SeedCacheAsync();

        var encrypted = await _service.EncryptAsync("plaintext-1", recipientPub);
        Assert.True(encrypted.Success);
        Assert.NotNull(encrypted.Value);

        var decrypted = await _service.DecryptAsync(encrypted.Value!, Salt);
        Assert.True(decrypted.Success);
        Assert.Equal("plaintext-1", decrypted.Value);
    }

    [Fact]
    public async Task DecryptAsync_AfterRemoveCachedKey_Fails()
    {
        var (keyId, recipientPub) = await SeedCacheAsync();
        var encrypted = await _service.EncryptAsync("vanish-after-evict", recipientPub);
        Assert.True(encrypted.Success);

        _provider.RemoveCachedKey(keyId);
        Assert.False(_provider.HasCachedKey(keyId));

        var decrypted = await _service.DecryptAsync(encrypted.Value!, Salt);
        Assert.False(decrypted.Success);
    }

    [Fact]
    public async Task DecryptAsync_DifferentSalt_Fails()
    {
        var (_, recipientPub) = await SeedCacheAsync();
        var encrypted = await _service.EncryptAsync("payload", recipientPub);
        Assert.True(encrypted.Success);

        // Decrypt under a salt the cache was never seeded with — the
        // keyId derived from "wrong-salt" misses, returning failure.
        var decrypted = await _service.DecryptAsync(encrypted.Value!, "wrong-salt");
        Assert.False(decrypted.Success);
    }

    [Fact]
    public async Task DecryptAsync_TamperedCiphertext_Fails()
    {
        var (_, recipientPub) = await SeedCacheAsync();
        var encrypted = await _service.EncryptAsync("integrity-test", recipientPub);
        Assert.True(encrypted.Success);

        // Flip a single byte in the ciphertext; AES-GCM auth tag should
        // reject it via DecryptAsymmetricWithKeyIdAsync.
        var tamperedBytes = Convert.FromBase64String(encrypted.Value!.Ciphertext);
        tamperedBytes[0] ^= 0x01;
        var tamperedCiphertext = Convert.ToBase64String(tamperedBytes);
        var tampered = new AsymmetricEncryptedData(
            encrypted.Value.EphemeralPublicKey,
            tamperedCiphertext,
            encrypted.Value.Nonce);

        var decrypted = await _service.DecryptAsync(tampered, Salt);
        Assert.False(decrypted.Success);
    }

    [Fact]
    public async Task SignAndEncrypt_DecryptAndVerify_RoundTrip()
    {
        var (_, recipientPub) = await SeedCacheAsync();
        var senderEd25519Pub = _publicKeyProvider.GetEd25519PublicKey()!;

        // Sign+encrypt: caller signs the plaintext with the cached
        // Ed25519 key (via SigningService → SignWithKeyIdAsync) and
        // wraps the resulting envelope in an ECIES blob for the
        // recipient (which happens to be ourselves in this fixture).
        var signedEncrypted = await _service.SignAndEncryptAsync(
            "signed-payload", recipientPub, _signingService, senderEd25519Pub, Salt);
        Assert.True(signedEncrypted.Success);

        var verified = await _service.DecryptAndVerifyAsync(
            signedEncrypted.Value!, Salt, _signingService);

        Assert.True(verified.Success);
        Assert.Equal("signed-payload", verified.Value!.Plaintext);
        Assert.Equal(senderEd25519Pub, verified.Value.SenderEd25519PublicKey);
        Assert.True(verified.Value.IsVerified);
    }
}
