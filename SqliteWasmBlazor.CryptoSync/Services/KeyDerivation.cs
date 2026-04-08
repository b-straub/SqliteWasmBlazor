using System.Security.Cryptography;
using System.Text;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Deterministic content-key derivation (resolved decision §15).
///
/// <para>
/// Per the CryptoSync architecture, the OWNER of a ShareGroup re-derives their
/// content key from their X25519 private key on demand — content keys are never
/// stored in plaintext at rest. Recipients of a scope receive the same content
/// key indirectly via their ShareTarget row, which carries the key
/// ECIES-wrapped to their X25519 public key.
/// </para>
///
/// <para>
/// Construction: HKDF-SHA256 with the admin/owner X25519 private key as input
/// keying material (IKM), no salt, and an info string that pins the purpose
/// and version. The version tag in the info string is the rotation lever — to
/// rotate keys without changing the owner's identity, bump the version (e.g.
/// <c>v1</c> → <c>v2</c>) and migrate.
/// </para>
/// </summary>
public static class KeyDerivation
{
    /// <summary>The well-known SharingId used for the system scope (admin's "system" group).</summary>
    public const string SystemSharingId = "system";

    private const string SystemContentKeyInfo = "SqliteWasmBlazor.CryptoSync.SystemContentKey.v1";
    private const string DomainContentKeyInfoPrefix = "SqliteWasmBlazor.CryptoSync.ContentKey.v1:";

    /// <summary>
    /// Length of all derived content keys in bytes (AES-256 → 32-byte key).
    /// </summary>
    public const int ContentKeyBytes = 32;

    /// <summary>
    /// Derive the deterministic content key for the admin's "system" ShareGroup.
    /// Same input always produces the same key. The admin re-derives this on
    /// demand whenever they need to encrypt or decrypt system-table rows.
    /// </summary>
    /// <param name="adminX25519PrivateKey">Admin's X25519 private key (32 bytes).</param>
    /// <returns>32-byte AES-GCM content key. Caller MUST zero after use.</returns>
    public static byte[] DeriveSystemContentKey(ReadOnlySpan<byte> adminX25519PrivateKey)
    {
        var output = new byte[ContentKeyBytes];
        HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: adminX25519PrivateKey,
            output: output,
            salt: ReadOnlySpan<byte>.Empty,
            info: Encoding.UTF8.GetBytes(SystemContentKeyInfo));
        return output;
    }

    /// <summary>
    /// Derive the deterministic content key for an arbitrary ShareGroup that
    /// this principal owns. Uses the scope id as part of the info string so
    /// distinct scopes derive distinct keys from the same private key.
    /// </summary>
    /// <param name="ownerX25519PrivateKey">Owner's X25519 private key (32 bytes).</param>
    /// <param name="scopeId">The ShareGroup identifier (e.g. <c>"groceries-main"</c>).</param>
    /// <returns>32-byte AES-GCM content key. Caller MUST zero after use.</returns>
    public static byte[] DeriveContentKey(ReadOnlySpan<byte> ownerX25519PrivateKey, string scopeId)
    {
        var output = new byte[ContentKeyBytes];
        HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: ownerX25519PrivateKey,
            output: output,
            salt: ReadOnlySpan<byte>.Empty,
            info: Encoding.UTF8.GetBytes(DomainContentKeyInfoPrefix + scopeId));
        return output;
    }
}
