using MessagePack;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Per-call metadata shipped from C# to the worker alongside every V2 bulk
/// export/import call. Carries everything the worker needs to derive content
/// keys (ECDH + HKDF via crypto-core), sign/verify envelopes, and route
/// the staged-apply pass.
///
/// <para>
/// <b>Security note.</b> <see cref="ClientX25519PrivateKey"/> and
/// <see cref="ClientEd25519PrivateKey"/> are sensitive. Callers MUST zero
/// backing buffers after the worker call returns (see <see cref="Clear"/>).
/// </para>
/// </summary>
[MessagePackObject]
public sealed class V2CryptoHeader
{
    /// <summary>Wire format version. Bumped on schema changes.</summary>
    [Key(0)]
    public int Version { get; set; } = 2;

    /// <summary>
    /// Table names (DbSet names) of all system tables. The worker uses this
    /// to partition groups into stage-1 (system) and stage-2 (domain) passes.
    /// </summary>
    [Key(1)]
    public List<string> SystemTables { get; set; } = [];

    /// <summary>
    /// This device's own <see cref="TrustedContact.Id"/>. Used by the worker
    /// to stamp shadow rows with sender identity.
    /// </summary>
    [Key(2)]
    public Guid ClientContactId { get; set; }

    /// <summary>
    /// This session's X25519 private key (32 bytes). Used by the worker to
    /// derive wrapping keys via ECDH + HKDF (crypto-core's deriveWrappingKey).
    /// </summary>
    [Key(3)]
    public byte[] ClientX25519PrivateKey { get; set; } = [];

    /// <summary>
    /// Group admin's X25519 public key (32 bytes). ECDH counterparty for
    /// wrapping key derivation: <c>deriveWrappingKey(myPrivKey, adminPubKey, groupContext)</c>.
    /// </summary>
    [Key(4)]
    public byte[] AdminX25519PublicKey { get; set; } = [];

    /// <summary>
    /// Versioned group context string, e.g. <c>"system:v1"</c>. Used as the
    /// HKDF info parameter for wrapping key derivation and bound as AAD
    /// during per-row encryption (Layer 1 tamper detection).
    /// </summary>
    [Key(5)]
    public string GroupContext { get; set; } = string.Empty;

    /// <summary>
    /// Current key version for CEK selection. Bound as AAD alongside
    /// <see cref="GroupContext"/> during per-row encryption.
    /// </summary>
    [Key(6)]
    public int KeyVersion { get; set; }

    /// <summary>
    /// This device's wrapped CEK for the group: <c>[nonce(12) | ciphertext]</c>.
    /// The worker unwraps it using the ECDH-derived wrapping key to obtain
    /// the 32-byte AES content encryption key.
    /// </summary>
    [Key(7)]
    public byte[] WrappedCek { get; set; } = [];

    /// <summary>
    /// This session's Ed25519 private key (32 bytes). Used by the worker to
    /// sign per-row envelopes on export (Layer 2 tamper detection).
    /// </summary>
    [Key(8)]
    public byte[] ClientEd25519PrivateKey { get; set; } = [];

    /// <summary>
    /// This session's Ed25519 public key (32 bytes). Included in the per-row
    /// envelope for signature verification on the receiver side.
    /// </summary>
    [Key(9)]
    public byte[] ClientEd25519PublicKey { get; set; } = [];

    /// <summary>
    /// Zero all sensitive key material buffers. Call in a <c>finally</c>
    /// block after the worker call returns.
    /// </summary>
    public void Clear()
    {
        if (ClientX25519PrivateKey.Length > 0)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ClientX25519PrivateKey);
        }
        if (ClientEd25519PrivateKey.Length > 0)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ClientEd25519PrivateKey);
        }
    }

    /// <summary>
    /// True if this table name is a system table for staged-apply routing.
    /// </summary>
    public bool IsSystemTable(string tableName)
    {
        foreach (var t in SystemTables)
        {
            if (t == tableName)
            {
                return true;
            }
        }
        return false;
    }
}
