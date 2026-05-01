using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.BouncyCastle;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

// Test double for the JS-side keyId cache that NobleCryptoProvider exposes
// via NobleInterop. Wraps a BouncyCastleCryptoProvider for the actual
// crypto operations, plus an in-memory dictionary that stands in for the
// JS SubtleCrypto/non-extractable key store.
//
// Used to drive the SigningService and AsymmetricEncryptionService
// keyId-cache round-trip tests in xUnit without booting a browser.
internal sealed class FakeKeyIdCryptoProvider : ICryptoProvider
{
    private readonly BouncyCastleCryptoProvider _inner = new();
    private readonly Dictionary<string, DualKeyPairFull> _cache = new();

    public string ProviderName => "FakeKeyIdCache";

    public bool SupportsKeyIdOperations => true;

    public IReadOnlyDictionary<string, DualKeyPairFull> Cache => _cache;

    // ============================================================
    // Key-id cache surface (the part being tested)
    // ============================================================

    public async ValueTask<PrfResult<DualKeyPair>> StoreKeysAsync(
        string keyId, ReadOnlyMemory<byte> prfSeed, int? ttlMs)
    {
        var dual = await _inner.DeriveDualKeyPairAsync(prfSeed).ConfigureAwait(false);
        _cache[keyId] = dual;
        _ = ttlMs;

        return PrfResult<DualKeyPair>.Ok(new DualKeyPair(
            dual.X25519PublicKey,
            dual.Ed25519PublicKey));
    }

    public ValueTask<PrfResult<DualKeyPair>> GetPublicKeysAsync(string keyId)
    {
        if (!_cache.TryGetValue(keyId, out var dual))
        {
            return ValueTask.FromResult(PrfResult<DualKeyPair>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED));
        }

        return ValueTask.FromResult(PrfResult<DualKeyPair>.Ok(new DualKeyPair(
            dual.X25519PublicKey,
            dual.Ed25519PublicKey)));
    }

    public bool HasCachedKey(string keyId) => _cache.ContainsKey(keyId);

    public void RemoveCachedKey(string keyId) => _cache.Remove(keyId);

    public async ValueTask<PrfResult<string>> SignWithKeyIdAsync(string message, string keyId)
    {
        if (!_cache.TryGetValue(keyId, out var dual))
        {
            return PrfResult<string>.Fail(PrfErrorCode.SIGNING_FAILED);
        }

        var ed25519Priv = Convert.FromBase64String(dual.Ed25519PrivateKey);
        return await _inner.SignAsync(message, ed25519Priv).ConfigureAwait(false);
    }

    public async ValueTask<PrfResult<string>> DecryptAsymmetricWithKeyIdAsync(
        AsymmetricEncryptedData asymmetricEncrypted, string keyId)
    {
        if (!_cache.TryGetValue(keyId, out var dual))
        {
            return PrfResult<string>.Fail(PrfErrorCode.DECRYPTION_FAILED);
        }

        var x25519Priv = Convert.FromBase64String(dual.X25519PrivateKey);
        return await _inner.DecryptAsymmetricAsync(asymmetricEncrypted, x25519Priv).ConfigureAwait(false);
    }

    // ============================================================
    // Pass-through to BouncyCastle for the non-keyId surface
    // ============================================================

    public ValueTask<PrfResult<SymmetricEncryptedData>> EncryptSymmetricAsync(
        string plaintext, ReadOnlyMemory<byte> key, string? associatedData = null) =>
        _inner.EncryptSymmetricAsync(plaintext, key, associatedData);

    public ValueTask<PrfResult<string>> DecryptSymmetricAsync(
        SymmetricEncryptedData encrypted, ReadOnlyMemory<byte> key, string? associatedData = null) =>
        _inner.DecryptSymmetricAsync(encrypted, key, associatedData);

    public ValueTask<PrfResult<AsymmetricEncryptedData>> EncryptAsymmetricAsync(
        string plaintext, string recipientPublicKeyBase64) =>
        _inner.EncryptAsymmetricAsync(plaintext, recipientPublicKeyBase64);

    public ValueTask<PrfResult<string>> DecryptAsymmetricAsync(
        AsymmetricEncryptedData asymmetricEncrypted, ReadOnlyMemory<byte> privateKey) =>
        _inner.DecryptAsymmetricAsync(asymmetricEncrypted, privateKey);

    public ValueTask<PrfResult<string>> SignAsync(string message, ReadOnlyMemory<byte> privateKey) =>
        _inner.SignAsync(message, privateKey);

    public ValueTask<bool> VerifyAsync(string message, string signatureBase64, string publicKeyBase64) =>
        _inner.VerifyAsync(message, signatureBase64, publicKeyBase64);

    public ValueTask<DualKeyPairFull> DeriveDualKeyPairAsync(ReadOnlyMemory<byte> prfSeed) =>
        _inner.DeriveDualKeyPairAsync(prfSeed);

    public ValueTask<string> GenerateSaltAsync(int length = 32) =>
        _inner.GenerateSaltAsync(length);

    public ValueTask<PrfResult<ReadOnlyMemory<byte>>> DeriveWrappingKeyAsync(
        ReadOnlyMemory<byte> ownPrivateKey, string recipientPublicKeyBase64, string context) =>
        _inner.DeriveWrappingKeyAsync(ownPrivateKey, recipientPublicKeyBase64, context);

    public ValueTask<ReadOnlyMemory<byte>> GenerateContentKeyAsync() =>
        _inner.GenerateContentKeyAsync();

    public ValueTask<PrfResult<SymmetricEncryptedData>> WrapContentKeyAsync(
        ReadOnlyMemory<byte> contentKey, ReadOnlyMemory<byte> wrappingKey) =>
        _inner.WrapContentKeyAsync(contentKey, wrappingKey);

    public ValueTask<PrfResult<ReadOnlyMemory<byte>>> UnwrapContentKeyAsync(
        SymmetricEncryptedData wrappedKey, ReadOnlyMemory<byte> wrappingKey) =>
        _inner.UnwrapContentKeyAsync(wrappedKey, wrappingKey);
}
