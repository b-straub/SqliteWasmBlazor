using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.CryptoSync.Abstractions;

namespace SqliteWasmBlazor.CryptoSync.UI.Components.Profile;

/// <summary>
/// Renders this device's local-only identity row (<see cref="DeviceSettings"/>):
/// device name, client GUID, admin / member role, optional WebAuthn credential
/// hint, and the resolved admin-contact-id on member devices. Read-only in
/// Stage 2 — edit affordances (device-name rename, etc.) come with the
/// post-Stage-2 demo step once the device row provisioning flow is wired
/// end-to-end.
///
/// <para>
/// The model fetches a fresh <see cref="DeviceSettings"/> snapshot on
/// <see cref="LoadProfile"/> via <see cref="DeviceIdentityService.GetAsync"/>
/// and projects the fields onto observable properties for binding. State is
/// reset to the unprovisioned defaults at the top of the command (pessimistic
/// init) so an unhandled exception leaves the panel rendering its
/// "not provisioned" branch automatically — no try/catch needed.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class UserProfileModel : ObservableModel
{
    public partial UserProfileModel(
        IDeviceIdentityService deviceIdentity,
        StatusModel statusModel,
        IStringLocalizer<UserProfileModel> localizer);

    public partial string? DeviceName { get; set; }
    public partial string? ClientGuid { get; set; }
    public partial string? CredentialId { get; set; }
    public partial bool IsAdmin { get; set; }
    public partial Guid? AdminContactId { get; set; }
    public partial Guid? OwnContactId { get; set; }
    public partial bool IsProvisioned { get; set; }

    [ObservableCommand(nameof(LoadProfileAsync), null, nameof(FormatLoadError))]
    public partial IObservableCommandAsync LoadProfile { get; }

    private async Task LoadProfileAsync()
    {
        // Pessimistic init — if the read throws, IsProvisioned stays false
        // and the panel renders the "not provisioned" alert.
        IsProvisioned = false;
        DeviceName = null;
        ClientGuid = null;
        CredentialId = null;
        IsAdmin = false;
        AdminContactId = null;
        OwnContactId = null;

        var settings = await DeviceIdentity.GetAsync();
        if (settings is null)
        {
            return;
        }

        DeviceName = settings.DeviceName;
        ClientGuid = settings.ClientGuid;
        CredentialId = settings.CredentialId;
        IsAdmin = settings.IsAdmin;
        AdminContactId = settings.AdminContactId;
        OwnContactId = settings.OwnContactId;
        IsProvisioned = true;
    }

    private string FormatLoadError(Exception ex) =>
        Localizer["Error_Load", ex.Message];
}
