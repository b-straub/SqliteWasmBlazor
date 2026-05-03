using Microsoft.AspNetCore.Components.Web;
using SqliteWasmBlazor.Demo.Models;

namespace SqliteWasmBlazor.Demo.Pages;

public partial class TodoList
{
    /// <summary>
    /// Initial DB stats refresh on page entry. Search-string / mode
    /// changes drive table reloads through <c>BumpReloadSignal</c> on the
    /// model + the SG-emitted <see cref="OnReloadSignalChangedAsync"/>
    /// hook below — no R3 subscriptions live here.
    /// </summary>
    protected override Task OnContextReadyAsync() =>
        Model.RefreshDatabaseFileSizeAsync();

    /// <summary>
    /// Triggered when <c>TodoListModel.ReloadSignal</c> changes (search /
    /// mode / add / delete / refresh). Replays through the
    /// <see cref="MudTable{T}.ReloadServerData"/> path which calls
    /// <see cref="TodoListModel.LoadServerDataAsync"/> with the current
    /// pagination state.
    /// </summary>
    protected override async Task OnReloadSignalChangedAsync(CancellationToken cancellationToken)
    {
        if (_table is { } table)
        {
            await table.ReloadServerData();
        }
    }

    private async Task HandleKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await Model.AddTodo.ExecuteAsync();
        }
    }
}
