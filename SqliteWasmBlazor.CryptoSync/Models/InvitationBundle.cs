using MessagePack;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Out-of-band invitation handout produced by
/// <see cref="ContactService.CreateInvitationAsync"/> on the admin device
/// and delivered to the contact via QR code, email, messenger, etc.
///
/// <para>
/// The bundle is the contact's only input for replying through
/// <c>ISyncTransport</c>: <see cref="Token"/> doubles as the match key
/// (admin finds the placeholder by token at <see cref="ContactStatus.Verified"/>
/// bind) and the AES-GCM PSK that opaques the response payload on the wire.
/// <see cref="AdminX25519PublicKey"/> tells the contact's transport which
/// public key to address the response to.
/// </para>
///
/// <para>
/// Wire size with default settings packs to ~80 bytes (32-byte token +
/// 44-byte Base64 admin pubkey + small header), well within QR-code limits.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class InvitationBundle
{
    /// <summary>Wire format version. Bumped on schema changes.</summary>
    [Key(0)]
    public int Version { get; set; } = 1;

    /// <summary>One-shot 32-byte secret bound to the placeholder
    /// <see cref="TrustedContact.InvitationToken"/>. Cleared on bind.</summary>
    [Key(1)]
    public required byte[] Token { get; init; }

    /// <summary>Admin's X25519 public key (Base64). Used by the contact's
    /// transport to address the response leg.</summary>
    [Key(2)]
    public required string AdminX25519PublicKey { get; init; }

    /// <summary>Optional relay URL hint. Null when the contact is expected
    /// to use a pre-configured relay or an in-band channel.</summary>
    [Key(3)]
    public string? RelayHint { get; init; }
}
