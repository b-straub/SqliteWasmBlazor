namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// Local-device identity helper. Tracks the singleton <see cref="DeviceSettings"/>
/// row, including the <c>IsAdmin</c> flag and the optional <c>AdminContactId</c>
/// (resolved on non-admin instances after the invitation handshake).
/// </summary>
public interface IDeviceIdentityService
{
    /// <summary>Returns true if this device is the admin (instance creator).</summary>
    ValueTask<bool> IsAdminAsync();

    /// <summary>
    /// Get this device's <see cref="DeviceSettings"/> row, or <c>null</c> if the
    /// instance has not been initialized yet.
    /// </summary>
    ValueTask<DeviceSettings?> GetAsync();

    /// <summary>Mark this device as the admin (instance creator). Idempotent.</summary>
    ValueTask MarkAsAdminAsync();

    /// <summary>
    /// Cache the admin's <see cref="TrustedContact.Id"/> on a non-admin device,
    /// learned from the invitation handshake.
    /// </summary>
    ValueTask SetAdminContactIdAsync(Guid contactId);

    /// <summary>
    /// Get the admin's <see cref="TrustedContact.Id"/> on a non-admin device,
    /// or <c>null</c> if the handshake hasn't completed yet (or this IS the admin).
    /// </summary>
    ValueTask<Guid?> GetAdminContactIdAsync();

    /// <summary>
    /// Cache this device's own <see cref="TrustedContact.Id"/> for the save
    /// interceptor's <c>SharingScope.CLIENT</c> resolution.
    /// </summary>
    ValueTask SetOwnContactIdAsync(Guid contactId);

    /// <summary>
    /// Get this device's own <see cref="TrustedContact.Id"/>, or <c>null</c>
    /// if it has not been resolved yet (non-admin device pre-first-sync).
    /// </summary>
    ValueTask<Guid?> GetOwnContactIdAsync();
}
