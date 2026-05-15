using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Per-member wrapped CEK — one row per member per group per key version.
/// Maps to the PDF persistence schema's <c>shareTargets</c> table.
///
/// <para>
/// The <see cref="WrappedContentKey"/> is an AES-256-GCM blob encrypted
/// with the HKDF-derived wrapping key (Layer 3 tamper detection). Modifying
/// the blob causes unwrap failure — the GCM authentication tag will not
/// verify.
/// </para>
///
/// <para>
/// System table — admin-managed. The generator emits a
/// <c>_crypto_ShareTargets</c> shadow table for encrypted sync.
/// </para>
/// </summary>
[SystemTable]
public sealed class ShareTarget : SyncableEntity
{
    /// <summary>FK to <see cref="ShareGroup"/>.</summary>
    public Guid ShareGroupId { get; set; }

    /// <summary>Navigation to the parent group.</summary>
    public ShareGroup? ShareGroup { get; set; }

    /// <summary>
    /// Key version this wrapped CEK belongs to. Must match
    /// <see cref="ShareGroup.KeyVersion"/> for current messages.
    /// Old versions are retained so old messages remain decryptable.
    /// </summary>
    public int KeyVersion { get; set; }

    /// <summary>
    /// X25519 public key of the member this CEK is wrapped for (Base64).
    /// Lookup key — "which groups can I decrypt?"
    /// </summary>
    [MaxLength(64)]
    public required string MemberPublicKey { get; set; }

    /// <summary>
    /// AES-256-GCM encrypted CEK blob: <c>[nonce(12) | ciphertext]</c>.
    /// Encrypted with the HKDF-derived wrapping key from
    /// <c>deriveWrappingKey(adminPrivateKey, memberPublicKey, groupContext)</c>.
    /// Modifying this blob causes unwrap failure (Layer 3 tamper detection).
    /// </summary>
    public required byte[] WrappedContentKey { get; set; }

    /// <summary>Role assigned to this member for this group.</summary>
    public SyncRole Role { get; set; }

    /// <summary>
    /// Ed25519 signature by the GroupAdmin over the canonical payload
    /// <c>MemberPublicKey | Role | GroupContext | KeyVersion</c>.
    /// Constitutes a signed credential: "I, GroupAdmin, grant this key
    /// this Role in this group at this KeyVersion." Verified at import
    /// time (Step 2b) against <see cref="GroupAdminEd25519PublicKey"/>.
    /// </summary>
    public byte[] AdminSignature { get; set; } = [];

    /// <summary>
    /// Ed25519 public key (Base64) of the GroupAdmin who signed this
    /// credential. Denormalized for worker-side verification without
    /// DB joins. Verified against <see cref="TrustedContact"/> at
    /// import time (Step 2c) to confirm the signer is trusted.
    /// </summary>
    [MaxLength(64)]
    public string GroupAdminEd25519PublicKey { get; set; } = "";

    /// <summary>FK to <see cref="TrustedContact"/> — who granted access.</summary>
    public Guid GrantedByContactId { get; set; }

    /// <summary>Navigation: the contact who granted access.</summary>
    public TrustedContact? GrantedByContact { get; set; }
}
