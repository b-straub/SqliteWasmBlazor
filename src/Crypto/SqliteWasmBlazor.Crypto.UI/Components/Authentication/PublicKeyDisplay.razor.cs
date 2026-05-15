using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MudBlazor;
using SqliteWasmBlazor.Crypto.Abstractions.Formatting;

namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

/// <summary>
/// Read-only public-key surface for any panel that wants to expose the
/// active PRF-derived public key to the user (copy out, share, paste into
/// a contact form). The component is stateless and modelless — the
/// consumer passes the cached pubkey as a parameter; reactivity is owned
/// by the parent <c>*ModelComponent</c> that injects
/// <see cref="AuthenticationModel"/> and re-renders on
/// <c>Auth.PublicKey</c> changes via the SG's cross-model dependency
/// tracking.
/// </summary>
public partial class PublicKeyDisplay
{
    /// <summary>
    /// Active PRF-derived public key (Base64 or armored). When null /
    /// empty, the component renders an "no key available" notice instead
    /// of the copy surface.
    /// </summary>
    [Parameter]
    public string? PublicKey { get; set; }

    /// <summary>
    /// WebAuthn credentialId associated with the displayed public key.
    /// Embedded in the armored payload so a recipient receiving this
    /// armored block can identify which passkey to authenticate with
    /// during a guided disk import.
    /// </summary>
    [Parameter]
    public string? CredentialId { get; set; }

    /// <summary>
    /// PFA-armored rendering of <see cref="PublicKey"/> — the format the
    /// rest of the app accepts on paste (recipient-key field, contact
    /// imports). Both the visible textfield and the clipboard write use
    /// this so what the user sees is exactly what gets copied.
    /// </summary>
    private string? Armored => string.IsNullOrEmpty(PublicKey)
        ? null
        : PrfArmor.ArmorPublicKey(PublicKey, BuildMetadata());

    private PublicKeyMetadata? BuildMetadata() =>
        string.IsNullOrEmpty(CredentialId)
            ? null
            : new PublicKeyMetadata { CredentialId = CredentialId };

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ISnackbar Snackbar { get; init; }

    [Inject]
    public required IStringLocalizer<PublicKeyDisplay> L { get; init; }

    private async Task CopyAsync()
    {
        if (Armored is not { } armored)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", armored);
            Snackbar.Add(L["Status_Copied"], Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add(L["Error_Copy", ex.Message], Severity.Error);
        }
    }
}
