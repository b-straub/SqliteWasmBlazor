using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.CryptoSync.Abstractions;
using SqliteWasmBlazor.CryptoSync.UI.Services;

namespace SqliteWasmBlazor.CryptoSync.UI.Components.Invitation;

/// <summary>
/// Composite invitation flow model — owns the create / accept / responses
/// surface. Talks to <see cref="ContactInvitationService"/> via the
/// host-supplied <see cref="IAdminInvitationContext"/> /
/// <see cref="IContactInvitationContext"/> seams that resolve the device's
/// WebAuthn-derived keys + salt + transport.
///
/// <para>
/// <b>Stage 2 status.</b> The panel ships in its three-file shape with
/// commands stubbed against the seams. Without a real
/// <see cref="IPrfAuthenticator"/>-backed seam impl (host-supplied,
/// post-Stage-2 demo step) the commands will surface a configuration
/// error rather than executing the underlying invitation flow. This is
/// deliberate — the Stage A wire contracts the service writes against
/// are stable; only the WebAuthn-key plumbing is missing.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class InvitationModel : ObservableModel
{
    public partial InvitationModel(
        IContactInvitationService invitationService,
        StatusModel statusModel,
        IStringLocalizer<InvitationModel> localizer);

    public partial string? FormUsername { get; set; }
    public partial string? FormEmail { get; set; }
    public partial string? FormComment { get; set; }
    public partial int IngestedCount { get; set; }

    [ObservableCommand(
        nameof(CreateInvitationAsync),
        nameof(CanCreateInvitation),
        nameof(FormatCreateError))]
    public partial IObservableCommandAsync<IAdminInvitationContext> CreateInvitation { get; }

    [ObservableCommand(nameof(IngestResponsesAsync), null, nameof(FormatIngestError))]
    public partial IObservableCommandAsync<IAdminInvitationContext> IngestResponses { get; }

    private bool CanCreateInvitation() => !string.IsNullOrWhiteSpace(FormUsername);

    private async Task CreateInvitationAsync(IAdminInvitationContext adminContext)
    {
        var adminKeys = await adminContext.GetAdminKeysAsync();
        var salt = await adminContext.GetDeploymentSaltBase64Async();

        var bundle = await InvitationService.CreateInvitationAsync(
            adminKeys,
            salt,
            FormUsername!,
            string.IsNullOrWhiteSpace(FormEmail) ? null : FormEmail,
            FormComment);

        StatusModel.AddSuccess(
            Localizer[
                "Status_InvitationCreated",
                FormUsername!,
                bundle.GroupId.ToString("N"),
                bundle.ExpiresAt.ToString("yyyy-MM-dd HH:mm")],
            nameof(CreateInvitation));
    }

    private async Task IngestResponsesAsync(IAdminInvitationContext adminContext)
    {
        var adminKeys = await adminContext.GetAdminKeysAsync();
        var transport = await adminContext.GetSyncTransportAsync();
        IngestedCount = await InvitationService.IngestInvitationResponsesAsync(adminKeys, transport);
        if (IngestedCount > 0)
        {
            StatusModel.AddInfo(
                Localizer["Status_ResponsesIngested", IngestedCount],
                nameof(IngestResponses));
        }
    }

    private string FormatCreateError(Exception ex) =>
        Localizer["Error_Create", ex.Message];

    private string FormatIngestError(Exception ex) =>
        Localizer["Error_Ingest", ex.Message];
}
