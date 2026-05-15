using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Group metadata — one row per sharing group. Maps to the PDF persistence
/// schema's <c>shareGroups</c> table. A "self" group has exactly one member
/// (the creator) and enables private encrypted storage using the same code
/// path as multi-member groups.
///
/// <para>
/// System table — admin-managed, synced to all peers. The generator emits
/// a <c>_crypto_ShareGroups</c> shadow table for encrypted sync.
/// </para>
///
/// <para>
/// <see cref="GroupContext"/> is the HKDF info parameter and is bound as AAD
/// during per-row encryption (Layer 1 tamper detection). <see cref="KeyVersion"/>
/// selects which <see cref="ShareTarget"/> wrapped CEK to use and is also
/// bound as AAD.
/// </para>
/// </summary>
[SystemTable]
public sealed class ShareGroup : SyncableEntity
{
    /// <summary>
    /// HKDF info parameter, e.g. <c>"group-{id}:v{N}"</c>. Bound as AAD
    /// during encryption — modifying it causes GCM auth tag failure
    /// (Layer 1 tamper detection).
    /// </summary>
    [MaxLength(256)]
    public required string GroupContext { get; set; }

    /// <summary>
    /// Current key version. Incremented on rotation (member removal).
    /// Old versions are retained in <see cref="ShareTarget"/> so old
    /// messages remain decryptable by remaining members.
    /// </summary>
    public int KeyVersion { get; set; }

    /// <summary>
    /// X25519 public key of the group admin (Base64). ECDH counterparty
    /// for CEK unwrapping — each member does
    /// <c>deriveWrappingKey(myPrivateKey, groupAdminPublicKey, groupContext)</c>
    /// to derive the wrapping key that unwraps their CEK.
    /// </summary>
    [MaxLength(64)]
    public required string GroupAdminPublicKey { get; set; }

    public DateTime CreatedAt { get; set; }
}
