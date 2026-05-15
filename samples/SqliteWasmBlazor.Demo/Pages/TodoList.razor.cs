using Microsoft.AspNetCore.Components.Web;
using SqliteWasmBlazor.Demo.Models;

namespace SqliteWasmBlazor.Demo.Pages;

public partial class TodoList
{
    /// <summary>
    /// Triggered when <c>TodoListModel.ReloadSignal</c> changes (search /
    /// mode / add / delete / refresh). Replays through the
    /// <see cref="MudTable{T}.ReloadServerData"/> path which calls
    /// <see cref="TodoListModel.LoadServerDataAsync"/> with the current
    /// pagination state.
    ///
    /// <para>
    /// <b>No initial DB-stats fill here.</b> Initial population is owned by
    /// the model (<see cref="TodoListModel.OnContextReadyAsync"/> +
    /// <c>OnDbStateChangedAsync</c>) so it stays gated on
    /// <see cref="DbStateModel.State"/> = READY — the page partial's
    /// <c>OnContextReady</c> runs at component init regardless of the
    /// encrypted-VFS lock state, which is exactly where the boot-time
    /// SQLITE_NOTADB race used to come from.
    /// </para>
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
