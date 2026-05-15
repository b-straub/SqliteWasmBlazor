using MessagePack;
using System.Security.Cryptography;

namespace SqliteWasmBlazor;

/// <summary>
/// Whole-disk export envelope. The encrypted VFS is mentally one disk
/// containing every registered DB file. <see cref="IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAsync"/>
/// bundles the entire encrypted SAH pool as a single MessagePack envelope of
/// this shape, and <see cref="IEncryptedSqliteWasmDatabaseService.ImportDiskAsync"/> preflights
/// one of these envelopes against the current disk state/key before wiping
/// the pool and unpacking it to replace the disk's contents.
///
/// <para>
/// <b>No per-file content-kind marker.</b> Plain vs ciphertext is
/// derivable from the first 16 bytes (SQLite magic <c>"SQLite format 3\0"</c>
/// for plain, ChaCha20 ciphertext otherwise). The bridge's existing
/// <see cref="SqliteWasmWorkerBridge.ImportDatabaseAsync"/> auto-routes
/// plain vs opaque imports via the same probe. The disk-level import first
/// checks that every file's kind matches the current disk state, and AEAD-
/// preflights encrypted files against their final path before deleting any
/// existing database.
/// </para>
///
/// <para>
/// <b>Security note.</b> <see cref="EncryptedDiskFile.Bytes"/> entries can be
/// either plain SQLite pages (legacy / test-constructed envelope) or AEAD-
/// sealed slot bytes (<see cref="IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAsync"/> output).
/// In both cases the producer / consumer should
/// <see cref="CryptographicOperations.ZeroMemory(byte[])"/> each entry's
/// buffer once it has been serialized into / deserialized out of the
/// envelope.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class EncryptedDiskEnvelope
{
    /// <summary>
    /// Wire format version. <c>2</c> is the asymmetric format: <see cref="Files"/>
    /// are slot ciphertext under <c>HKDF(ECDH(ephemeralPriv, recipientPub), …)</c>,
    /// and the matching ephemeral pubkey + ECIES-wrapped content key are
    /// carried in <see cref="EphemeralPublicKey"/> /
    /// <see cref="WrappedContentKeyCiphertext"/> / <see cref="WrappedContentKeyNonce"/>.
    /// Recipient unwraps with their PRF-derived X25519 private key.
    /// </summary>
    [Key(0)]
    public int Version { get; set; } = 2;

    /// <summary>
    /// AAD prefix version expected for any per-page AEAD inside the bundled
    /// ciphertext slots. Must match the worker's <c>buildPageAad</c>
    /// constant (currently <c>"v1"</c>).
    /// </summary>
    [Key(1)]
    public string AadVersion { get; set; } = "v1";

    /// <summary>Every DB file in the source pool, ordered by name.</summary>
    [Key(2)]
    public List<EncryptedDiskFile> Files { get; set; } = new();

    /// <summary>
    /// Sender-generated ephemeral X25519 public key (Base64) used during
    /// the ECIES wrap of <see cref="WrappedContentKeyCiphertext"/>.
    /// </summary>
    [Key(3)]
    public string EphemeralPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// AES-256-GCM ciphertext + tag (Base64) of the per-export 32-byte
    /// content key (<c>K_wrap</c>). Wrap key derived from
    /// <c>HKDF(ECDH(ephemeralPriv, recipientPub), …)</c>; recipient
    /// inverts via <c>HKDF(ECDH(recipientPriv, ephemeralPub), …)</c>.
    /// </summary>
    [Key(4)]
    public string WrappedContentKeyCiphertext { get; set; } = string.Empty;

    /// <summary>
    /// AES-256-GCM nonce (Base64, 12 bytes) for
    /// <see cref="WrappedContentKeyCiphertext"/>.
    /// </summary>
    [Key(5)]
    public string WrappedContentKeyNonce { get; set; } = string.Empty;

    /// <summary>
    /// WebAuthn credentialId (Base64) of the passkey whose PRF-derived X25519
    /// keypair the envelope was sealed to. The guided-import flow uses this
    /// to drive WebAuthn's <c>allowCredentials</c> so the recipient is
    /// prompted with the exact passkey needed to unwrap K_wrap. Sender's own
    /// credentialId for "backup to self"; recipient's credentialId (carried
    /// in the recipient's armored PFA PUBLIC KEY) for "share with peer".
    /// </summary>
    [Key(6)]
    public string CredentialIdHint { get; set; } = string.Empty;

    /// <summary>
    /// No-op on v2 envelopes. Per-file <see cref="EncryptedDiskFile.Bytes"/>
    /// is page ciphertext under a per-export ECIES-derived K_wrap — not
    /// sensitive plaintext, no security reason to zero. Kept as a no-op
    /// because zeroing here on a buffer that MessagePack-CSharp's
    /// serializer aliases into the returned envelope byte[] would erase
    /// the on-the-wire ciphertext (observed: receiver sees all-zero
    /// dbBytes despite intact length and intact wrap key).
    /// </summary>
    public void Clear()
    {
        // intentionally empty — see XML doc above.
    }
}

/// <summary>
/// One file in a <see cref="EncryptedDiskEnvelope"/> — bare DB name + raw bytes
/// (either plain SQLite pages or slot-format ciphertext). See
/// <see cref="EncryptedDiskEnvelope"/> for the kind-detection contract.
/// </summary>
[MessagePackObject]
public sealed class EncryptedDiskFile
{
    /// <summary>Bare DB name as it appears in the SAH pool, e.g. <c>"TodoDb.db"</c>.</summary>
    [Key(0)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Raw bytes for this file — either plain SQLite pages (4096-byte
    /// multiples) or slot-format ciphertext (4124-byte multiples).
    /// </summary>
    [Key(1)]
    public byte[] Bytes { get; set; } = [];
}
