using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Abstractions;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Local-device identity helper. Tracks the singleton <see cref="DeviceSettings"/>
/// row, including the <c>IsAdmin</c> flag and the optional <c>AdminContactId</c>
/// (resolved on non-admin instances after the invitation handshake).
///
/// <para>
/// Admin-only operations (<c>InvitationService</c> writes,
/// <c>ContactPromotionService.ElevateToFullAsync</c>, ownership-transfer refusal
/// for system scopes) gate on <see cref="IsAdminAsync"/>.
/// </para>
/// </summary>
internal class DeviceIdentityService(CryptoSyncContextBase context) : IDeviceIdentityService
{
    /// <summary>
    /// Returns true if this device is the admin (instance creator).
    /// </summary>
    public async ValueTask<bool> IsAdminAsync()
    {
        var settings = await context.DeviceSettings.FirstOrDefaultAsync();
        return settings?.IsAdmin ?? false;
    }

    /// <summary>
    /// Get this device's <see cref="DeviceSettings"/> row, or <c>null</c> if the
    /// instance has not been initialized yet.
    /// </summary>
    public ValueTask<DeviceSettings?> GetAsync()
    {
        return new ValueTask<DeviceSettings?>(context.DeviceSettings.FirstOrDefaultAsync());
    }

    /// <summary>
    /// Mark this device as the admin (instance creator). Idempotent. Throws if
    /// no <see cref="DeviceSettings"/> row exists yet — the consuming app must
    /// create the device row at first launch before calling this.
    /// </summary>
    public async ValueTask MarkAsAdminAsync()
    {
        var settings = await context.DeviceSettings.FirstOrDefaultAsync()
            ?? throw new InvalidOperationException(
                "DeviceSettings row not found. Create the device row at first launch before calling MarkAsAdminAsync.");

        settings.IsAdmin = true;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Cache the admin's <see cref="TrustedContact.Id"/> on a non-admin device,
    /// learned from the invitation handshake. Lets peers know whose key is the
    /// system-table owner.
    /// </summary>
    public async ValueTask SetAdminContactIdAsync(Guid contactId)
    {
        var settings = await context.DeviceSettings.FirstOrDefaultAsync()
            ?? throw new InvalidOperationException(
                "DeviceSettings row not found. Create the device row at first launch before calling SetAdminContactIdAsync.");

        settings.AdminContactId = contactId;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Get the admin's <see cref="TrustedContact.Id"/> on a non-admin device,
    /// or <c>null</c> if the handshake hasn't completed yet (or this IS the admin).
    /// </summary>
    public async ValueTask<Guid?> GetAdminContactIdAsync()
    {
        var settings = await context.DeviceSettings.FirstOrDefaultAsync();
        return settings?.AdminContactId;
    }

    /// <summary>
    /// Cache this device's own <see cref="TrustedContact.Id"/> on
    /// <see cref="DeviceSettings.OwnContactId"/>. Required by the save
    /// interceptor to resolve "my self-group SharingId" for new
    /// <see cref="SharingScope.CLIENT"/>-scoped rows. On the admin device
    /// this is set during bootstrap (admin's own contact id). On a
    /// non-admin device the app layer calls this after the first sync
    /// pulls the device's own <see cref="TrustedContact"/> row, matching
    /// it by X25519 public key.
    /// </summary>
    public async ValueTask SetOwnContactIdAsync(Guid contactId)
    {
        var settings = await context.DeviceSettings.FirstOrDefaultAsync()
            ?? throw new InvalidOperationException(
                "DeviceSettings row not found. Create the device row at first launch before calling SetOwnContactIdAsync.");

        settings.OwnContactId = contactId;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Get this device's own <see cref="TrustedContact.Id"/>, or <c>null</c>
    /// if it has not been resolved yet (non-admin device pre-first-sync).
    /// </summary>
    public async ValueTask<Guid?> GetOwnContactIdAsync()
    {
        var settings = await context.DeviceSettings.FirstOrDefaultAsync();
        return settings?.OwnContactId;
    }
}
