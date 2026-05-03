using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MudBlazor;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Extensions;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Demo.Models;

/// <summary>
/// FTS5 highlight rendering mode. Was previously the local
/// <c>TodoSearchComponent.SearchDisplayMode</c> enum — moved here so the
/// markup can drop the <see cref="Microsoft.AspNetCore.Components.ParameterAttribute"/>
/// + <see cref="Microsoft.AspNetCore.Components.EventCallback{TValue}"/> bridge
/// pattern called out by <c>feedback_host_must_be_rxblazor.md</c>.
/// </summary>
public enum SearchDisplayMode
{
    NORMAL,
    HIGHLIGHT,
    SNIPPET,
}

/// <summary>
/// Drives the <c>TodoList.razor</c> page. Owns the new-todo form state,
/// the FTS5 search controls (search string / display mode / query mode),
/// and the dataset summary (total count + DB file size). Server-side row
/// loading goes through <see cref="LoadServerDataAsync"/> which the
/// markup wires straight into <c>MudTable.ServerData</c>.
///
/// <para>
/// <b>Reload signal pattern (intentional caveat).</b> Search-string / mode
/// changes trigger a counter increment via <see cref="BumpReloadSignal"/>;
/// the counter is declared <c>[ObservableComponentTriggerAsync]</c> and
/// the page partial calls <c>MudTable.ReloadServerData()</c> from the
/// SG-emitted <c>OnReloadSignalChangedAsync</c> hook. The RxBlazorV2 audit
/// rules (<c>reactive-patterns.md</c> §6) discourage counter / toggle
/// properties used as notification signals and ask for a semantic status
/// property instead. We accept the stylistic deviation here because the
/// consumer side effect (re-running an opaque MudTable's server-data
/// callback) genuinely has no semantic state to surface — there's nothing
/// to encode beyond "do it again." The alternative is fanning out
/// <c>[ObservableComponentTriggerAsync]</c> across each of <see cref="SearchString"/> /
/// <see cref="SearchMode"/> / <see cref="QueryMode"/> + the add / delete /
/// toggle paths and implementing 5+ near-identical hooks, all of which
/// would call <c>ReloadServerData()</c>. The counter wins on DRY.
/// </para>
///
/// <para>
/// <b>Search debounce.</b> Each keystroke fires the reload signal so an
/// in-flight FTS5 query is cancelled (<see cref="MudTable"/> passes the
/// caller's <see cref="CancellationToken"/> through). The cancel-on-
/// retrigger comes for free from the table's debouncer + SQLite query
/// cancellation; we don't add an explicit timer here. If load latency
/// becomes a problem on huge DBs, swap to an R3 <c>Throttle</c> on the
/// SearchString observable inside <c>OnContextReadyAsync</c>.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class TodoListModel : ObservableModel
{
    public const string DatabaseName = "TodoDb.db";

    public partial TodoListModel(
        IDbContextFactory<TodoDbContext> contextFactory,
        StatusModel statusModel,
        IStringLocalizer<TodoListModel> localizer);

    public partial string NewTitle { get; set; } = string.Empty;
    public partial string NewDescription { get; set; } = string.Empty;

    [ObservableTrigger(nameof(BumpReloadSignal))]
    public partial string SearchString { get; set; } = string.Empty;

    [ObservableTrigger(nameof(BumpReloadSignal))]
    public partial SearchDisplayMode SearchMode { get; set; } = SearchDisplayMode.NORMAL;

    [ObservableTrigger(nameof(BumpReloadSignal))]
    public partial Fts5QueryMode QueryMode { get; set; } = Fts5QueryMode.PROCESSED;

    public partial int TotalCount { get; set; }
    public partial long DatabaseFileSize { get; set; }

    /// <summary>
    /// Bumped whenever the table needs to refetch (search-string change,
    /// add/delete/toggle, or explicit refresh). The page partial observes
    /// this via the SG-emitted <c>OnReloadSignalChangedAsync</c> hook and
    /// calls <c>MudTable.ReloadServerData()</c>.
    /// </summary>
    [ObservableComponentTriggerAsync]
    public partial int ReloadSignal { get; set; }

    /// <summary>
    /// Toggle between processed and raw FTS5 query mode. Backs the
    /// <c>MudSwitch</c> in the markup; binding direct to the enum
    /// property is awkward for switches.
    /// </summary>
    public bool IsRawMode
    {
        get => QueryMode == Fts5QueryMode.RAW;
        set => QueryMode = value ? Fts5QueryMode.RAW : Fts5QueryMode.PROCESSED;
    }

    public bool HasActiveSearch => !string.IsNullOrWhiteSpace(SearchString);

    [ObservableCommand(nameof(AddTodoAsync), nameof(CanAddTodo), nameof(FormatOperationError))]
    public partial IObservableCommandAsync AddTodo { get; }

    [ObservableCommand(nameof(ToggleCompleteAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync<TodoItem> ToggleComplete { get; }

    [ObservableCommand(nameof(DeleteTodoAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync<TodoItem> DeleteTodo { get; }

    [ObservableCommand(nameof(RefreshListAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync RefreshList { get; }

    [ObservableCommand(nameof(ClearSearch))]
    public partial IObservableCommand ClearSearchCommand { get; }

    private bool CanAddTodo() => !string.IsNullOrWhiteSpace(NewTitle);

    private void BumpReloadSignal() => ReloadSignal++;

    /// <summary>
    /// Cache of FTS5 highlighted/snippet content keyed by row id. Refilled
    /// inside <see cref="LoadServerDataAsync"/>; the markup pulls from it
    /// via <see cref="GetHighlightedText"/>. Plain <see cref="Dictionary{TKey, TValue}"/>
    /// because per-render lookups don't need observation.
    /// </summary>
    private readonly Dictionary<Guid, (string Title, string Description)> _highlightCache = new();

    /// <summary>
    /// MudTable's <c>ServerData</c> callback. Wires the model's search
    /// state into the FTS5 extensions on <see cref="TodoDbContext"/> and
    /// returns a paginated <see cref="TableData{T}"/>. Errors land in
    /// <see cref="StatusModel"/> via the model's status sink — the
    /// callback returns an empty page so the UI renders cleanly.
    /// </summary>
    public async Task<TableData<TodoItem>> LoadServerDataAsync(
        TableState state,
        CancellationToken cancellationToken)
    {
        await using var context = await ContextFactory.CreateDbContextAsync(cancellationToken);

        _highlightCache.Clear();

        try
        {
            if (string.IsNullOrWhiteSpace(SearchString))
            {
                var query = context.TodoItems
                    .Where(t => !t.IsDeleted)
                    .OrderByDescending(t => t.UpdatedAt);
                var count = await query.CountAsync(cancellationToken);
                TotalCount = count;
                var data = await query
                    .Skip(state.Page * state.PageSize)
                    .Take(state.PageSize)
                    .ToListAsync(cancellationToken);
                return new TableData<TodoItem> { Items = data, TotalItems = count };
            }

            return SearchMode switch
            {
                SearchDisplayMode.HIGHLIGHT =>
                    await LoadHighlightedAsync(context, state, cancellationToken),
                SearchDisplayMode.SNIPPET =>
                    await LoadSnippetAsync(context, state, cancellationToken),
                _ =>
                    await LoadPlainSearchAsync(context, state, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            var formatted = ex.Message.Contains("fts5: syntax error")
                ? Localizer["Error_Fts5Syntax", ExtractFts5Error(ex.Message)]
                : Localizer["Error_Search", ex.Message];
            StatusModel.AddError(formatted, nameof(LoadServerDataAsync));
            TotalCount = 0;
            return new TableData<TodoItem> { Items = new List<TodoItem>(), TotalItems = 0 };
        }
    }

    private async Task<TableData<TodoItem>> LoadHighlightedAsync(
        TodoDbContext context, TableState state, CancellationToken cancellationToken)
    {
        var query = context.SearchTodoItemsWithHighlight(SearchString, "<mark>", "</mark>", QueryMode);
        var count = await query.CountAsync(cancellationToken);
        TotalCount = count;
        var rows = await query
            .Skip(state.Page * state.PageSize)
            .Take(state.PageSize)
            .ToListAsync(cancellationToken);
        foreach (var r in rows)
        {
            _highlightCache[r.Id] = (r.DisplayTitle, r.DisplayDescription);
        }
        return new TableData<TodoItem>
        {
            Items = rows.Cast<TodoItem>().ToList(),
            TotalItems = count,
        };
    }

    private async Task<TableData<TodoItem>> LoadSnippetAsync(
        TodoDbContext context, TableState state, CancellationToken cancellationToken)
    {
        var query = context.SearchTodoItemsWithSnippet(
            SearchString, "<mark>", "</mark>", "...", 5, QueryMode);
        var count = await query.CountAsync(cancellationToken);
        TotalCount = count;
        var rows = await query
            .Skip(state.Page * state.PageSize)
            .Take(state.PageSize)
            .ToListAsync(cancellationToken);
        foreach (var r in rows)
        {
            _highlightCache[r.Id] = (r.DisplayTitleSnippet, r.DisplayDescriptionSnippet);
        }
        return new TableData<TodoItem>
        {
            Items = rows.Cast<TodoItem>().ToList(),
            TotalItems = count,
        };
    }

    private async Task<TableData<TodoItem>> LoadPlainSearchAsync(
        TodoDbContext context, TableState state, CancellationToken cancellationToken)
    {
        var query = context.SearchTodoItems(SearchString, QueryMode);
        var count = await query.CountAsync(cancellationToken);
        TotalCount = count;
        var data = await query
            .Skip(state.Page * state.PageSize)
            .Take(state.PageSize)
            .ToListAsync(cancellationToken);
        return new TableData<TodoItem> { Items = data, TotalItems = count };
    }

    /// <summary>
    /// Lookup helper called from the markup's <c>HighlightedText</c> binding.
    /// Returns null when the search mode is <see cref="SearchDisplayMode.NORMAL"/>
    /// or the row didn't appear in the most recent FTS5 result page.
    /// </summary>
    public string? GetHighlightedText(Guid itemId, bool isTitle)
    {
        if (_highlightCache.TryGetValue(itemId, out var cached))
        {
            return isTitle ? cached.Title : cached.Description;
        }
        return null;
    }

    private async Task AddTodoAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var todo = new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = NewTitle,
            Description = NewDescription,
            UpdatedAt = DateTime.UtcNow,
            IsCompleted = false,
        };
        await using (var context = await ContextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.TodoItems.Add(todo);
            await context.SaveChangesAsync(cancellationToken);
        }
        stopwatch.Stop();

        NewTitle = string.Empty;
        NewDescription = string.Empty;
        BumpReloadSignal();
        await RefreshDatabaseFileSizeAsync(cancellationToken);
        StatusModel.AddSuccess(
            Localizer["Status_TodoAdded", stopwatch.ElapsedMilliseconds],
            nameof(AddTodo));
    }

    private async Task ToggleCompleteAsync(TodoItem todo, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        todo.IsCompleted = !todo.IsCompleted;
        todo.CompletedAt = todo.IsCompleted ? DateTime.UtcNow : null;
        await using (var context = await ContextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.TodoItems.Update(todo);
            await context.SaveChangesAsync(cancellationToken);
        }
        stopwatch.Stop();

        BumpReloadSignal();
        var statusKey = todo.IsCompleted ? "Status_TodoCompleted" : "Status_TodoReopened";
        StatusModel.AddSuccess(
            Localizer[statusKey, stopwatch.ElapsedMilliseconds],
            nameof(ToggleComplete));
    }

    private async Task DeleteTodoAsync(TodoItem todo, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using (var context = await ContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var tracked = await context.TodoItems.FindAsync([todo.Id], cancellationToken);
            if (tracked is not null)
            {
                tracked.IsDeleted = true;
                tracked.DeletedAt = DateTime.UtcNow;
            }
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                StatusModel.AddWarning(
                    Localizer["Status_AlreadyDeleted"],
                    nameof(DeleteTodo));
                BumpReloadSignal();
                return;
            }
        }
        stopwatch.Stop();

        BumpReloadSignal();
        await RefreshDatabaseFileSizeAsync(cancellationToken);
        StatusModel.AddWarning(
            Localizer["Status_TodoDeleted", stopwatch.ElapsedMilliseconds],
            nameof(DeleteTodo));
    }

    private async Task RefreshListAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        BumpReloadSignal();
        await RefreshDatabaseFileSizeAsync(cancellationToken);
        stopwatch.Stop();
        StatusModel.AddSuccess(
            Localizer["Status_ListRefreshed", stopwatch.ElapsedMilliseconds],
            nameof(RefreshList));
    }

    private void ClearSearch()
    {
        SearchString = string.Empty;
        // Trigger on SearchString fires BumpReloadSignal automatically.
    }

    /// <summary>
    /// One-time DB stats refresh on page entry; subsequent updates piggy-
    /// back on add/delete/refresh paths. Page partial calls this from
    /// <c>OnContextReadyAsync</c>.
    /// </summary>
    public async Task RefreshDatabaseFileSizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await ContextFactory.CreateDbContextAsync(cancellationToken);
            var fileSize = await context.Database.SqlQueryRaw<long>(
                "SELECT (SELECT page_count FROM pragma_page_count()) * (SELECT page_size FROM pragma_page_size()) AS Value")
                .SingleOrDefaultAsync(cancellationToken);
            DatabaseFileSize = fileSize;
        }
        catch
        {
            DatabaseFileSize = 0;
        }
    }

    private string FormatOperationError(Exception ex) =>
        Localizer["Error_Operation", ex.Message];

    private static string ExtractFts5Error(string fullErrorMessage)
    {
        var fts5Index = fullErrorMessage.IndexOf("fts5:", StringComparison.OrdinalIgnoreCase);
        if (fts5Index < 0)
        {
            return fullErrorMessage;
        }
        var errorPart = fullErrorMessage[(fts5Index + 5)..].Trim();
        if (!errorPart.Contains("syntax error near"))
        {
            return errorPart;
        }
        if (fullErrorMessage.Contains("NEAR"))
        {
            return $"{errorPart}. NEAR uses 'NEAR(term1 term2, distance)'.";
        }
        return $"{errorPart}. Column filters use 'title:term'.";
    }
}
