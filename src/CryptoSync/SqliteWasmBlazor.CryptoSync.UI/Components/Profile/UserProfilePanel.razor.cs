namespace SqliteWasmBlazor.CryptoSync.UI.Components.Profile;

/// <summary>
/// Component-side glue for <see cref="UserProfileModel"/>: triggers an
/// initial profile load when the model context becomes ready. The model
/// owns all state and the load command — this partial is a one-liner.
/// </summary>
public partial class UserProfilePanel
{
    protected override async Task OnContextReadyAsync()
    {
        await Model.LoadProfile.ExecuteAsync();
    }
}
