using MessagePack;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// One row in a sync delta — exactly the shape of a row in the
/// <c>_crypto_&lt;table&gt;</c> shadow table. The shadow exists so a node
/// can forward rows to peers under the group CEK without decrypt+re-encrypt;
/// at-rest confidentiality is a separate concern handled by the PRF-keyed
/// VFS. Per-row encryption: the row's MessagePack representation is
/// AES-GCM encrypted under the scope's content key with a fresh per-row
/// <see cref="Nonce"/>; the result lives in <see cref="EncryptedRow"/>.
///
/// <para>
/// <see cref="Id"/>, <see cref="SharingScope"/>, and <see cref="SharingId"/>
/// stay outside the AEAD body so the receiver can route, filter, and store
/// shadow rows without holding the group CEK. (Disk pages still travel
/// through the encrypted VFS — these fields are not exposed to relays.)
/// Decisions §14 (same role-on-ShareTarget principle), §15 (deterministic
/// content keys), §17 (full re-encryption on revoke).
/// </para>
/// </summary>
[MessagePackObject]
public sealed class ShadowRow
{
    /// <summary>The plaintext primary key — shared with the corresponding open-table row.</summary>
    [Key(0)]
    public Guid Id { get; set; }

    /// <summary>0=Public, 1=Shared, 2=Client.</summary>
    [Key(1)]
    public int SharingScope { get; set; }

    /// <summary>The scope identifier (e.g. <c>"system"</c>, <c>"groceries-main"</c>).</summary>
    [Key(2)]
    public string SharingId { get; set; } = string.Empty;

    /// <summary>AES-GCM ciphertext (includes the 16-byte authentication tag).</summary>
    [Key(3)]
    public byte[] EncryptedRow { get; set; } = [];

    /// <summary>12-byte AES-GCM nonce, fresh per row.</summary>
    [Key(4)]
    public byte[] Nonce { get; set; } = [];

    /// <summary>Which CEK version encrypted this row. Bound as AAD during
    /// encryption (Layer 1 tamper detection). Receiver uses this to select
    /// the correct <see cref="ShareTarget"/> wrapped CEK.</summary>
    [Key(5)]
    public int KeyVersion { get; set; }

    /// <summary>Ed25519 public key of the row producer (Base64). Used for
    /// per-row envelope signature verification (Layer 2).</summary>
    [Key(6)]
    public string SenderPublicKey { get; set; } = string.Empty;

    /// <summary>Ed25519 signature over the canonical per-row envelope
    /// (Layer 2 tamper detection). Covers Id, GroupId, KeyVersion,
    /// SenderPublicKey, and SHA-256(EncryptedRow).</summary>
    [Key(7)]
    public byte[] EnvelopeSignature { get; set; } = [];
}

/// <summary>
/// All <see cref="ShadowRow"/>s for one table inside a delta. Bundled by
/// table so the receiver can do the staged apply: process system-table
/// groups (<see cref="IsSystemTable"/> = true) first, then domain table
/// groups, with content keys learned from any new <c>SharingKey</c> rows
/// that landed in the system pass.
/// </summary>
[MessagePackObject]
public sealed class ShadowRowGroup
{
    /// <summary>The DbSet name (e.g. <c>"Contacts"</c>, <c>"CryptoTestItems"</c>).</summary>
    [Key(0)]
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// True if this group is for a system table (TrustedContact, SyncPermission,
    /// SharingKey, …). Receiver routes system-table groups to the stage 1
    /// pass; domain-table groups go through stage 2 with the now-updated
    /// sharing state from stage 1.
    /// </summary>
    [Key(1)]
    public bool IsSystemTable { get; set; }

    [Key(2)]
    public List<ShadowRow> Rows { get; set; } = [];
}

/// <summary>
/// Outer envelope shipped between actors via the relay. Header (sender
/// public key, signature) wraps the body (<see cref="Groups"/> — each
/// group's rows are individually encrypted under their scope's content
/// key). The relay sees only what the sender chooses to put outside the
/// per-row AEAD; the at-rest VFS layer plays no role here because the
/// envelope leaves the device.
///
/// <para>
/// Wire format = the shadow rows themselves. There is NO secondary batch
/// encryption of the whole payload; the per-row encryption inside each
/// <see cref="ShadowRow.EncryptedRow"/> is the only confidentiality layer
/// on the wire. The sender's Ed25519 signature over the MessagePack-
/// serialized <see cref="Groups"/> field (NOT the whole envelope — just
/// the body) gives integrity + authenticity.
/// </para>
///
/// <para>
/// This shape was settled in the Stage 3 / Phase D-3 design discussion:
/// no batch envelope, no double crypto work, shadow IS the wire format.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class DeltaEnvelope
{
    /// <summary>Wire format version. Bumped on schema changes.</summary>
    [Key(0)]
    public int Version { get; set; } = 1;

    /// <summary>Sender's Ed25519 public key (Base64). Receiver uses this to
    /// look up the sender in the local Contacts table for the trust gate.</summary>
    [Key(1)]
    public string SenderEd25519PublicKey { get; set; } = string.Empty;

    /// <summary>Ed25519 signature over the MessagePack-serialized
    /// <see cref="Groups"/> bytes. Verified by the receiver before any
    /// staged apply work.</summary>
    [Key(2)]
    public byte[] SenderSignature { get; set; } = [];

    /// <summary>The body — table groups in send order. Convention: the sender
    /// emits system-table groups first, then domain-table groups, but the
    /// receiver routes by <see cref="ShadowRowGroup.IsSystemTable"/> regardless
    /// so order on the wire is advisory.</summary>
    [Key(3)]
    public List<ShadowRowGroup> Groups { get; set; } = [];
}
