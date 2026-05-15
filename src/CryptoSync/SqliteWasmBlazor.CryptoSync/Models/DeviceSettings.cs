using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Local-only device identity and configuration. Not synced.
/// </summary>
public sealed class DeviceSettings
{
    public Guid Id { get; set; }

    [MaxLength(64)]
    public required string ClientGuid { get; set; }

    [MaxLength(128)]
    public required string DeviceName { get; set; }

    /// <summary>WebAuthn credential ID hint for auto-fill.</summary>
    [MaxLength(256)]
    public string? CredentialId { get; set; }

    /// <summary>
    /// True on the instance creator (admin) device. Set explicitly via
    /// <c>DeviceIdentityService.MarkAsAdminAsync</c> at first launch.
    /// Admin-only operations (invitation creation, system-table writes,
    /// ownership transfer refusal for system scopes) gate on this flag.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// On non-admin devices: the resolved <c>TrustedContact.Id</c> of the admin,
    /// learned via the invitation handshake. Lets peers know whose key is the
    /// system-table owner. Null on the admin device itself.
    /// </summary>
    public Guid? AdminContactId { get; set; }

    /// <summary>
    /// This device's own <see cref="TrustedContact.Id"/>. Used by the save
    /// interceptor to resolve "my self-group SharingId" at save time. On the
    /// admin device this equals the admin contact id (set during bootstrap).
    /// On a non-admin device the app layer sets this after the first sync
    /// pulls the device's own contact row (matched by X25519 public key).
    /// </summary>
    public Guid? OwnContactId { get; set; }
}
