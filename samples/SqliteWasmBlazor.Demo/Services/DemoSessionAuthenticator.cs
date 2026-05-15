using Microsoft.AspNetCore.Components;
using SqliteWasmBlazor.Crypto.UI.Components.Authentication;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Demo.Services;

/// <summary>
/// Demo-side <see cref="ISessionAuthenticator"/> backing the
/// <c>SessionExpiredPopover</c>. Required even when the popover isn't
/// mounted in <c>MainLayout</c>, because <c>AddCryptoUI()</c> registers
/// <c>SessionExpiredPopoverModel</c> as Scoped and the DI validator
/// fails the whole graph if its constructor seam is unsatisfiable.
///
/// <para>
/// <b>ReAuthenticate</b> wipes the cached PRF session so the existing
/// <c>AuthenticationPanel</c> re-enables and the user can derive keys
/// again. <b>Dismiss</b> navigates to the home page — same effect a
/// "leave this surface" gesture would have on a real consumer app.
/// </para>
/// </summary>
public sealed class DemoSessionAuthenticator : ISessionAuthenticator
{
    private readonly AuthenticationModel _auth;
    private readonly NavigationManager _navigation;

    public DemoSessionAuthenticator(
        AuthenticationModel auth,
        NavigationManager navigation)
    {
        _auth = auth;
        _navigation = navigation;
    }

    public ValueTask ReAuthenticateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _auth.ClearKeys();
        return ValueTask.CompletedTask;
    }

    public ValueTask DismissAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _navigation.NavigateTo("");
        return ValueTask.CompletedTask;
    }
}
