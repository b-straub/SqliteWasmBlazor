using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Models;

namespace SqliteWasmBlazor.CryptoSync.UI.Components.Contacts;

/// <summary>
/// Backing model for <see cref="ContactsPanel"/>. Owns the loaded
/// <see cref="TrustedContact"/> list, the per-row soft-delete command,
/// and a pubkey copy-request signal.
///
/// <para>
/// <b>Scope.</b> Stage-2 absorption is intentionally read + local-soft-delete
/// only. End-to-end admin revocation (rotate every shared group, push
/// <c>Revoke</c> on the whitelist) requires <c>DualKeyPairFull</c> admin
/// keys and the deployment salt, neither of which a panel may hold. Real
/// admin tooling (post-Stage-2 admin-keyless seeding step) wraps that flow
/// and exposes a parameterized callback into this model if desired.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class ContactsModel : ObservableModel
{
    public partial ContactsModel(
        ContactService contactService,
        StatusModel statusModel,
        IStringLocalizer<ContactsModel> localizer);

    public partial IReadOnlyList<TrustedContact>? Contacts { get; set; }

    /// <summary>
    /// Pending copy-to-clipboard signal. Set by <see cref="RequestCopyKey"/>;
    /// the component's component-trigger override does the JS interop +
    /// snackbar, then clears the signal so the next click re-fires.
    /// </summary>
    [ObservableComponentTriggerAsync]
    public partial CopyKeyRequest? PendingCopy { get; set; }

    [ObservableCommand(nameof(LoadContactsAsync), null, nameof(FormatLoadError))]
    public partial IObservableCommandAsync LoadContacts { get; }

    [ObservableCommand(nameof(DeleteContactAsync), null, nameof(FormatDeleteError))]
    public partial IObservableCommandAsync<Guid> DeleteContact { get; }

    [ObservableCommand(nameof(RequestCopyKey))]
    public partial IObservableCommand<TrustedContact> RequestCopyKeyCommand { get; }

    private async Task LoadContactsAsync()
    {
        // Pessimistic init — if GetAllAsync throws, Contacts stays empty
        // and the panel renders the "no contacts" alert.
        Contacts = Array.Empty<TrustedContact>();
        var list = await ContactService.GetAllAsync();
        Contacts = list
            .Where(c => !c.IsDeleted)
            .OrderByDescending(c => c.IsAdmin)
            .ThenBy(c => c.Username)
            .ToList();
    }

    private async Task DeleteContactAsync(Guid contactId)
    {
        await ContactService.DeleteAsync(contactId);
        await LoadContactsAsync();
    }

    private void RequestCopyKey(TrustedContact contact) =>
        PendingCopy = new CopyKeyRequest(contact.X25519PublicKey, contact.Username);

    private string FormatLoadError(Exception ex) =>
        Localizer["Error_Load", ex.Message];

    private string FormatDeleteError(Exception ex) =>
        Localizer["Error_Delete", ex.Message];
}
