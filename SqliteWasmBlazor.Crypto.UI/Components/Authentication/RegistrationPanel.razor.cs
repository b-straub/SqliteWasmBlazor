namespace SqliteWasmBlazor.Crypto.UI.Components.Authentication;

public partial class RegistrationPanel
{
    protected override async Task OnContextReadyAsync()
    {
        await Model.CheckPrfSupport.ExecuteAsync();
    }
}
