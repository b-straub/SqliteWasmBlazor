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

    /// <summary>Optional relay URL hint.</summary>
    [MessagePack.Key(6)]
    public string? RelayHint { get; init; }
}

/// <summary>Thrown when an invitation bundle's signature doesn't verify.</summary>
public sealed class InvalidInvitationBundleException(string message) : InvalidOperationException(message);

/// <summary>Thrown when an invitation bundle is past its expiry.</summary>
public sealed class InvitationExpiredException(string message) : InvalidOperationException(message);
