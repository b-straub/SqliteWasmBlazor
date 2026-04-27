using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Abstractions;

/// <summary>
/// Abstraction for cryptographic operations using AES-256-GCM.
/// Implementations: BouncyCastleCryptoProvider, NobleCryptoProvider.
/// </summary>
public interface ICryptoProvider
{
    /// <summary>
    /// Gets the name of this crypto provider for diagnostics.
    /// </summary>
    string ProviderName { get; }

    // ============================================================
    // SYMMETRIC ENCRYPTION (AES-256-GCM)
    // ============================================================

    /// <summary>
    /// Encrypts a message using AES-256-GCM.
    /// </summary>
    /// <param name="plaintext">The plaintext to encrypt</param>
    /// <param name="key">32-byte encryption key</param>
    /// <param name="associatedData">Optional AAD bound to the ciphertext (e.g., group context)</param>
    /// <returns>Encrypted message with nonce</returns>
    ValueTask<PrfResult<SymmetricEncryptedData>> EncryptSymmetricAsync(
        string plaintext,
        ReadOnlyMemory<byte> key,
        string? associatedData = null);

    /// <summary>
    /// Decrypts a message using AES-256-GCM.
    /// </summary>
    /// <param name="encrypted">The encrypted message</param>
    /// <param name="key">32-byte encryption key</param>
    /// <param name="associatedData">Optional AAD that must match the value used during encryption</param>
    /// <returns>Decrypted plaintext</returns>
    ValueTask<PrfResult<string>> DecryptSymmetricAsync(
        SymmetricEncryptedData encrypted,
        ReadOnlyMemory<byte> key,
        string? associatedData = null);

    // ============================================================
    // ASYMMETRIC ENCRYPTION (ECIES: X25519 + AES-256-GCM)
    // ============================================================

    /// <summary>
    /// Encrypts a message using ECIES (X25519 key agreement + AES-256-GCM).
    /// </summary>
    /// <param name="plaintext">The plaintext to encrypt</param>
    /// <param name="recipientPublicKeyBase64">Recipient's X25519 public key</param>
    /// <returns>Encrypted message with ephemeral public key and nonce</returns>
    ValueTask<PrfResult<AsymmetricEncryptedData>> EncryptAsymmetricAsync(
        string plaintext,
        string recipientPublicKeyBase64);

    /// <summary>
    /// Decrypts a message using ECIES (X25519 key agreement + AES-256-GCM).
    /// </summary>
    /// <param name="asymmetricEncrypted">The encrypted message</param>
    /// <param name="privateKey">Recipient's X25519 private key (32 bytes)</param>
    /// <returns>Decrypted plaintext</returns>
    ValueTask<PrfResult<string>> DecryptAsymmetricAsync(
        AsymmetricEncryptedData asymmetricEncrypted,
        ReadOnlyMemory<byte> privateKey);

    // ============================================================
    // ED25519 DIGITAL SIGNATURES
    // ============================================================

    /// <summary>
    /// Signs a message with an Ed25519 private key.
    /// </summary>
    /// <param name="message">The message to sign</param>
    /// <param name="privateKey">Ed25519 private key (32-byte seed)</param>
    /// <returns>Base64-encoded signature (64 bytes)</returns>
    ValueTask<PrfResult<string>> SignAsync(
        string message,
        ReadOnlyMemory<byte> privateKey);

    /// <summary>
    /// Verifies an Ed25519 signature.
    /// </summary>
    /// <param name="message">The original message</param>
    /// <param name="signatureBase64">Base64-encoded signature</param>
    /// <param name="publicKeyBase64">Base64-encoded Ed25519 public key</param>
    /// <returns>True if signature is valid</returns>
    ValueTask<bool> VerifyAsync(
        string message,
        string signatureBase64,
        string publicKeyBase64);

    // ============================================================
    // KEY GENERATION & DERIVATION
    // ============================================================

    /// <summary>
    /// Derives an X25519 keypair from a PRF seed.
    /// </summary>
    /// <param name="prfSeed">32-byte PRF seed</param>
    /// <returns>X25519 keypair</returns>
    ValueTask<KeyPair> DeriveX25519KeyPairAsync(ReadOnlyMemory<byte> prfSeed);

    /// <summary>
    /// Derives an Ed25519 keypair from a PRF seed.
    /// </summary>
    /// <param name="prfSeed">32-byte PRF seed</param>
    /// <returns>Ed25519 keypair</returns>
    ValueTask<KeyPair> DeriveEd25519KeyPairAsync(ReadOnlyMemory<byte> prfSeed);

    /// <summary>
    /// Derives both X25519 and Ed25519 keypairs from a single PRF seed.
    /// </summary>
    /// <param name="prfSeed">32-byte PRF seed</param>
    /// <returns>Both keypairs</returns>
    ValueTask<DualKeyPairFull> DeriveDualKeyPairAsync(ReadOnlyMemory<byte> prfSeed);

    /// <summary>
    /// Generates a cryptographically secure random salt.
    /// </summary>
    /// <param name="length">Length in bytes</param>
    /// <returns>Base64-encoded random salt</returns>
    ValueTask<string> GenerateSaltAsync(int length = 32);

    /// <summary>
    /// Derives a wrapping key via X25519 ECDH + HKDF-SHA256.
    /// Combines key agreement and key derivation in one step — no raw shared secret exposure.
    /// </summary>
    /// <param name="ownPrivateKey">Own X25519 private key (32 bytes)</param>
    /// <param name="recipientPublicKeyBase64">Counterparty's X25519 public key (Base64)</param>
    /// <param name="context">HKDF info string for domain separation (e.g., "group-abc:v1")</param>
    /// <returns>32-byte wrapping key</returns>
    ValueTask<PrfResult<ReadOnlyMemory<byte>>> DeriveWrappingKeyAsync(
        ReadOnlyMemory<byte> ownPrivateKey,
        string recipientPublicKeyBase64,
        string context);

    /// <summary>
    /// Generates a random 32-byte content encryption key.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> GenerateContentKeyAsync();

    /// <summary>
    /// Wraps (encrypts) a content key with a wrapping key using AES-256-GCM.
    /// </summary>
    /// <param name="contentKey">The content key to wrap (32 bytes)</param>
    /// <param name="wrappingKey">The wrapping key derived from ECDH + HKDF (32 bytes)</param>
    /// <returns>Wrapped content key as AES-GCM blob (nonce + ciphertext + tag)</returns>
    ValueTask<PrfResult<SymmetricEncryptedData>> WrapContentKeyAsync(
        ReadOnlyMemory<byte> contentKey,
        ReadOnlyMemory<byte> wrappingKey);

    /// <summary>
    /// Unwraps (decrypts) a content key using a wrapping key.
    /// </summary>
    /// <param name="wrappedKey">The wrapped content key blob</param>
    /// <param name="wrappingKey">The wrapping key derived from ECDH + HKDF (32 bytes)</param>
    /// <returns>The unwrapped 32-byte content key</returns>
    ValueTask<PrfResult<ReadOnlyMemory<byte>>> UnwrapContentKeyAsync(
        SymmetricEncryptedData wrappedKey,
        ReadOnlyMemory<byte> wrappingKey);

    // ============================================================
    // KEY-ID BASED OPERATIONS (Optional - for providers with JS key caching)
    // ============================================================

    /// <summary>
    /// Indicates whether this provider supports keyId-based operations.
    /// When true, keys can be cached internally (e.g., in JS) and operations
    /// can be performed using keyId instead of passing key bytes.
    /// </summary>
    bool SupportsKeyIdOperations => false;

    /// <summary>
    /// Stores and derives keys from PRF seed, caching them by keyId.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    /// <param name="keyId">Unique identifier for the cached keys</param>
    /// <param name="prfSeed">32-byte PRF seed</param>
    /// <param name="ttlMs">Time-to-live in milliseconds, null for no expiration</param>
    /// <returns>Public keys (X25519 and Ed25519)</returns>
    ValueTask<PrfResult<DualKeyPair>> StoreKeysAsync(string keyId, ReadOnlyMemory<byte> prfSeed, int? ttlMs) =>
        ValueTask.FromResult(PrfResult<DualKeyPair>.Fail(PrfErrorCode.NOT_SUPPORTED));

    /// <summary>
    /// Gets the public keys for a cached key set.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    ValueTask<PrfResult<DualKeyPair>> GetPublicKeysAsync(string keyId) =>
        ValueTask.FromResult(PrfResult<DualKeyPair>.Fail(PrfErrorCode.NOT_SUPPORTED));

    /// <summary>
    /// Checks if keys are cached for the given keyId.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    bool HasCachedKey(string keyId) => false;

    /// <summary>
    /// Removes cached keys for the given keyId.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    void RemoveCachedKey(string keyId) { }

    /// <summary>
    /// Signs a message using cached Ed25519 key.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    ValueTask<PrfResult<string>> SignWithKeyIdAsync(string message, string keyId) =>
        ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.NOT_SUPPORTED));

    /// <summary>
    /// Encrypts using cached symmetric key.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    ValueTask<PrfResult<SymmetricEncryptedData>> EncryptSymmetricWithKeyIdAsync(
        string plaintext,
        string keyId,
        string? associatedData = null) =>
        ValueTask.FromResult(PrfResult<SymmetricEncryptedData>.Fail(PrfErrorCode.NOT_SUPPORTED));

    /// <summary>
    /// Decrypts using cached symmetric key.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    ValueTask<PrfResult<string>> DecryptSymmetricWithKeyIdAsync(
        SymmetricEncryptedData encrypted,
        string keyId,
        string? associatedData = null) =>
        ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.NOT_SUPPORTED));

    /// <summary>
    /// Decrypts asymmetrically using cached X25519 private key.
    /// Only available when <see cref="SupportsKeyIdOperations"/> is true.
    /// </summary>
    ValueTask<PrfResult<string>> DecryptAsymmetricWithKeyIdAsync(
        AsymmetricEncryptedData asymmetricEncrypted,
        string keyId) =>
        ValueTask.FromResult(PrfResult<string>.Fail(PrfErrorCode.NOT_SUPPORTED));
}
