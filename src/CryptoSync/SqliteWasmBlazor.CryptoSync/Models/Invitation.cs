using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Admin-initiated invitation. Persisted as a row in the invitation
/// <see cref="ShareGroup"/> (one ShareTarget bound to a transport keypair
/// derived from the bundle's shared secret). Status is implicit:
/// <list type="bullet">
///   <item>Row exists with null contact pubkeys → pending.</item>
///   <item>Row has non-null pubkeys + valid <see cref="ContactSignature"/> → responded.</item>
///   <item>Row missing → expired/revoked/promoted (all collapse to deletion).</item>
/// </list>
///
/// <para>
/// <b>Routing:</b> rows ride the invitation share group's CEK so only admin
/// + the invitee's transport keypair can decrypt. <see cref="SyncableEntity.SharingScope"/>
/// is set to <see cref="SharingScope.SHARED"/>; <see cref="SyncableEntity.SharingId"/>
/// is set to the invitation share group's <see cref="ShareGroup.GroupContext"/>
/// by <see cref="ContactInvitationService.CreateInvitationAsync"/>. The
/// <see cref="SystemTableAttribute"/> exists only so the table participates in
/// the seeded Owner/Editor/Viewer permissions (see
/// <see cref="CryptoSyncContextBase.GetSystemPermissions"/>); the interceptor
/// respects an explicit <see cref="SyncableEntity.SharingId"/> on
/// <see cref="SystemTableAttribute"/>-marked rows.
/// </para>
/// </summary>
[SystemTable]
public sealed class Invitation : SyncableEntity
{
    [MaxLength(128)]
    public required string Username { get; set; }

    [MaxLength(256)]
    public string? Email { get; set; }

    [MaxLength(512)]
    public string? Comment { get; set; }

    /// <summary>Filled by invitee on response. Null = pending.</summary>
    [MaxLength(64)]
    public string? ContactX25519PublicKey { get; set; }

    /// <summary>Filled by invitee on response. Null = pending.</summary>
    [MaxLength(64)]
    public string? ContactEd25519PublicKey { get; set; }

    /// <summary>
    /// Ed25519 signature over canonical
    /// <c>(Id || ContactX25519PublicKey || ContactEd25519PublicKey || ExpiresAt.Ticks)</c>
    /// produced by the invitee's Ed25519 key.
    /// </summary>
    public byte[]? ContactSignature { get; set; }

    /// <summary>Invitee's pre-built self-group ID (privacy invariant — admin can't unwrap).</summary>
    public Guid? SelfGroupId { get; set; }

    [MaxLength(128)]
    public string? SelfGroupContext { get; set; }

    public int? SelfKeyVersion { get; set; }

    public byte[]? SelfWrappedContentKey { get; set; }

    public byte[]? SelfShareTargetSignature { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Admin-side persistence of the transport keypair's Ed25519 public key
    /// (Base64). Admin pushes <c>sha256(salt || this)</c> onto the relay
    /// whitelist at CreateInvitation time so the invitee can POST during the
    /// bootstrap window; at PromoteInvitation time admin revokes that hash
    /// and adds the contact's real Ed25519 hash, in a single push. Null on
    /// invitations created before the whitelist hooks landed.
    /// </summary>
    [MaxLength(64)]
    public string? TransportEd25519PublicKey { get; set; }
}

/// <summary>
/// Out-of-band handout produced by
/// <see cref="ContactInvitationService.CreateInvitationAsync"/>. Carries
/// the 32-byte transport secret (interpreted on both sides as an X25519
/// private key) plus admin-side metadata signed by the admin's Ed25519
/// key. Delivered via QR / email / messenger.
/// </summary>
[MessagePack.MessagePackObject]
public sealed class InvitationBundle
{
    /// <summary>Wire format version. Bumped on schema changes.</summary>
    [MessagePack.Key(0)]
    public int Version { get; set; } = 2;

    /// <summary>32-byte shared secret. Both sides interpret it as an X25519
    /// private key and derive the transport public key locally.</summary>
    [MessagePack.Key(1)]
    public required byte[] TransportSecret { get; init; }

    /// <summary>Identity of the invitation <see cref="ShareGroup"/>.</summary>
    [MessagePack.Key(2)]
    public required Guid GroupId { get; init; }

    /// <summary>UTC expiry deadline.</summary>
    [MessagePack.Key(3)]
    public required DateTime ExpiresAt { get; init; }

    /// <summary>Admin's Ed25519 signature over canonical
    /// <c>(transportPub || GroupId.ToByteArray() || ExpiresAt.Ticks)</c>.</summary>
    [MessagePack.Key(4)]
    public required byte[] AdminSignature { get; init; }

    /// <summary>Admin's Ed25519 public key (Base64) used to verify <see cref="AdminSignature"/>.</summary>
    [MessagePack.Key(5)]
    public required string AdminEd25519PublicKey { get; init; }

    /// <summary>Admin's X25519 public key (Base64). Invitee uses this as the
    /// ECDH counterparty when wrapping the response payload.</summary>
    [MessagePack.Key(6)]
    public required string AdminX25519PublicKey { get; init; }

    /// <summary>Optional relay URL hint.</summary>
    [MessagePack.Key(7)]
    public string? RelayHint { get; init; }
}

/// <summary>Thrown when an invitation bundle's signature doesn't verify.</summary>
public sealed class InvalidInvitationBundleException(string message) : InvalidOperationException(message);

/// <summary>Thrown when an invitation bundle is past its expiry.</summary>
public sealed class InvitationExpiredException(string message) : InvalidOperationException(message);

/// <summary>Thrown when the contact's signature on an invitation response doesn't verify.</summary>
public sealed class InvalidInvitationResponseException(string message) : InvalidOperationException(message);

/// <summary>Thrown when promoting an invitation that doesn't exist.</summary>
public sealed class InvitationNotFoundException(string message) : InvalidOperationException(message);

/// <summary>Thrown when promoting an invitation that hasn't been responded to yet.</summary>
public sealed class InvitationNotRespondedException(string message) : InvalidOperationException(message);

/// <summary>
/// Wire-level envelope the invitee posts back to the admin. Carries the
/// MessagePack-encoded <see cref="InvitationResponsePayload"/> AES-256-GCM
/// encrypted under <c>HKDF(ECDH(transportPriv, adminX25519Pub),
/// info=invitationGroupContext)</c> — the same wrapping-key derivation
/// admin uses to decrypt with <c>(adminPriv, transportPub)</c>.
/// </summary>
[MessagePack.MessagePackObject]
public sealed class InvitationResponseEnvelope
{
    [MessagePack.Key(0)] public int Version { get; set; } = 1;

    /// <summary>Routing key — admin uses this to find the matching
    /// <see cref="ShareGroup"/> and the transport <see cref="ShareTarget"/>.</summary>
    [MessagePack.Key(1)] public required Guid GroupId { get; init; }

    /// <summary>AES-256-GCM ciphertext + auth tag.</summary>
    [MessagePack.Key(2)] public required byte[] Ciphertext { get; init; }

    /// <summary>AES-256-GCM nonce (12 bytes).</summary>
    [MessagePack.Key(3)] public required byte[] Nonce { get; init; }
}

/// <summary>
/// Inner plaintext of an <see cref="InvitationResponseEnvelope"/>. Carries
/// the contact's identity + pre-built self-group rows + Ed25519 signature
/// over <c>InvitationId || ContactX25519 || ContactEd25519 || ExpiresAt.Ticks</c>.
/// </summary>
[MessagePack.MessagePackObject]
public sealed class InvitationResponsePayload
{
    [MessagePack.Key(0)] public int Version { get; set; } = 1;

    [MessagePack.Key(1)] public required string ContactX25519PublicKey { get; init; }

    [MessagePack.Key(2)] public required string ContactEd25519PublicKey { get; init; }

    [MessagePack.Key(3)] public required Guid SelfGroupId { get; init; }

    [MessagePack.Key(4)] public required string SelfGroupContext { get; init; }

    [MessagePack.Key(5)] public required int SelfKeyVersion { get; init; }

    [MessagePack.Key(6)] public required byte[] SelfWrappedContentKey { get; init; }

    [MessagePack.Key(7)] public required byte[] SelfShareTargetSignature { get; init; }

    /// <summary>
    /// Ed25519 signature over canonical
    /// <c>InvitationId || ContactX25519PublicKey || ContactEd25519PublicKey || ExpiresAt.Ticks</c>.
    /// Verified by admin against <see cref="ContactEd25519PublicKey"/>.
    /// </summary>
    [MessagePack.Key(8)] public required byte[] ContactSignature { get; init; }
}
