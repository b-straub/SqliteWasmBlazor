using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MudBlazor;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI;
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
/// <b>One batch, one hook.</b> <c>MudTable.ServerData</c> is a pull API —
/// it refetches only when something calls <c>ReloadServerData()</c>, never
/// on a re-render. Everything that decides what the table shows carries
/// <c>[ObservableComponentBatchAsync(<see cref="ListingBatchId"/>)]</c>, so
/// the generator emits exactly one hook,
/// <c>OnListingBatchChangedAsync</c>, and the page partial calls
/// <c>ReloadServerData()</c> from there. The batch is dispatched with
/// <c>AwaitOperation.Switch</c>: a newer change releases the running hook
/// at once, and its <c>ReloadServerData()</c> makes MudTable cancel the
/// in-flight fetch through the token it hands to
/// <see cref="LoadServerDataAsync"/>.
/// </para>
///
/// <para>
/// <b>The window is per property.</b> <see cref="SearchString"/> declares
/// <see cref="SearchDebounceMs"/> so a burst of keystrokes reaches the
/// database once, when typing stops. The mode toggles and
/// <see cref="LastChange"/> declare none — each is a single deliberate
/// event with no burst to settle — and reload immediately. Same batch,
/// same hook; only the timing differs.
/// </para>
///
/// <para>
/// <b>Publish results, never inputs.</b> <see cref="LoadServerDataAsync"/>
/// writes <see cref="TotalCount"/> and the highlight cache, neither of
/// which is a batch member. Writing one from inside the callback would ask
/// the table to reload itself.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class TodoListModel : ObservableModel
{
    public const string DatabaseName = "TodoDb.db";

    /// <summary>
    /// Batch id shared by everything that decides what the table shows.
    /// Also the name the generated hook derives from:
    /// <c>OnListingBatchChangedAsync</c>.
    /// </summary>
    public const string ListingBatchId = "listing";

    /// <summary>
    /// Trailing debounce window for the typed search term, in milliseconds.
    /// </summary>
    public const int SearchDebounceMs = 300;

    public partial TodoListModel(
        IDbContextFactory<TodoDbContext> contextFactory,
        DbStateModel dbState,
        StatusModel statusModel,
        IStringLocalizer<TodoListModel> localizer);

    public partial string NewTitle { get; set; } = string.Empty;
    public partial string NewDescription { get; set; } = string.Empty;

    /// <summary>
    /// Typed input, so it declares a window: only the settled term reaches
    /// SQLite. R3's trailing-edge debounce lives in the generated
    /// subscription — there is no <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// and no CancellationTokenSource to write here.
    /// </summary>
    [ObservableComponentBatchAsync(ListingBatchId, SearchDebounceMs)]
    public partial string SearchString { get; set; } = string.Empty;

    /// <summary>
    /// A radio click is one deliberate action, so it carries no window and
    /// re-runs the query at once under the current term.
    /// </summary>
    [ObservableComponentBatchAsync(ListingBatchId)]
    public partial SearchDisplayMode SearchMode { get; set; } = SearchDisplayMode.NORMAL;

    /// <summary>
    /// Same batch, same immediacy as <see cref="SearchMode"/>: switching
    /// between processed and raw FTS5 syntax re-runs the current term.
    /// </summary>
    [ObservableComponentBatchAsync(ListingBatchId)]
    public partial Fts5QueryMode QueryMode { get; set; } = Fts5QueryMode.PROCESSED;

    /// <summary>
    /// What the last completed mutation did. Set by the add / toggle /
    /// delete / refresh commands, never by <see cref="LoadServerDataAsync"/>,
    /// and a batch member because the table's contents are a function of
    /// the query <i>and</i> the data behind it.
    ///
    /// <para>
    /// This is the semantic-completion property of
    /// <c>reactive-patterns.md</c> §6, not the counter of §6b: it says what
    /// happened rather than standing in for "do it again". Both fields
    /// earn their place — a record setter is equality-guarded, so without
    /// <see cref="TodoChange.At"/> a second toggle of the same row, or a
    /// second press of Refresh, would compare equal to the first and
    /// publish nothing.
    /// </para>
    /// </summary>
    [ObservableComponentBatchAsync(ListingBatchId)]
    public partial TodoChange? LastChange { get; set; }

    /// <summary>
    /// Row count of the most recent page fetch. A <i>result</i>, written by
    /// <see cref="LoadServerDataAsync"/>, so deliberately not a batch
    /// member.
    /// </summary>
    public partial int TotalCount { get; set; }

    public partial long DatabaseFileSize { get; set; }

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
        catch (PoolLockedException)
        {
            // Race-safe path: user hit Lock while a query was in flight.
            // AuthorizeView re-renders to NotAuthorized on the next state
            // tick; surface a transient localized notice and return empty.
            StatusModel.AddWarning(
                Localizer["Error_PoolLocked"], nameof(LoadServerDataAsync));
            TotalCount = 0;
            return new TableData<TodoItem> { Items = new List<TodoItem>(), TotalItems = 0 };
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
        LastChange = new TodoChange(TodoChangeKind.ADDED, DateTime.UtcNow);
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

        LastChange = new TodoChange(TodoChangeKind.TOGGLED, DateTime.UtcNow);
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
            // A row the list rendered but the key can't reach means the stored
            // Id isn't in the form the provider binds. Reporting success here
            // would leave the row on screen with a "deleted" notice next to it.
            var tracked = await context.TodoItems.FindAsync([todo.Id], cancellationToken)
                          ?? throw new InvalidOperationException(
                              $"No TodoItem with Id {todo.Id} — the row is not addressable by its key.");

            tracked.IsDeleted = true;
            tracked.DeletedAt = DateTime.UtcNow;

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                StatusModel.AddWarning(
                    Localizer["Status_AlreadyDeleted"],
                    nameof(DeleteTodo));
                LastChange = new TodoChange(TodoChangeKind.DELETED, DateTime.UtcNow);
                return;
            }
        }

        stopwatch.Stop();

        LastChange = new TodoChange(TodoChangeKind.DELETED, DateTime.UtcNow);
        await RefreshDatabaseFileSizeAsync(cancellationToken);
        StatusModel.AddWarning(
            Localizer["Status_TodoDeleted", stopwatch.ElapsedMilliseconds],
            nameof(DeleteTodo));
    }

    private async Task RefreshListAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        LastChange = new TodoChange(TodoChangeKind.REFRESHED, DateTime.UtcNow);
        await RefreshDatabaseFileSizeAsync(cancellationToken);
        stopwatch.Stop();
        StatusModel.AddSuccess(
            Localizer["Status_ListRefreshed", stopwatch.ElapsedMilliseconds],
            nameof(RefreshList));
    }

    private void ClearSearch()
    {
        // A batch member, so this reaches OnListingBatchChangedAsync on its
        // own — after the debounce window, like any other change to the term.
        SearchString = string.Empty;
    }

    /// <summary>
    /// DB stats refresh. Self-guarded on <see cref="DbStateModel.State"/> ==
    /// READY so it no-ops while the encrypted VFS is locked — mirrors the
    /// "domain code never touches a locked disk" rule. Called from the
    /// model's own <see cref="OnContextReadyAsync"/> (covers the
    /// already-READY-at-construction case) and from
    /// <see cref="OnDbStateChangedAsync"/> (covers the
    /// becomes-READY-after-construction case).
    /// </summary>
    public async Task RefreshDatabaseFileSizeAsync(CancellationToken cancellationToken = default)
    {
        if (DbState.State != DbInitState.READY)
        {
            DatabaseFileSize = 0;
            return;
        }

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

    /// <summary>
    /// Initial stats fill — covers "DbState is already READY at construction
    /// time" (e.g., user navigates to /todos after unlocking elsewhere).
    /// The internal observer below handles state TRANSITIONS into READY.
    /// </summary>
    protected override async Task OnContextReadyAsync(CancellationToken cancellationToken)
    {
        await RefreshDatabaseFileSizeAsync(cancellationToken);
    }

    /// <summary>
    /// Auto-detected internal observer (RxBlazorV2 §7) — touches
    /// <see cref="DbStateModel.State"/>, so the SG wires this method to fire
    /// on every state transition. Refresh the DB stats whenever the disk
    /// becomes available; the self-guard in
    /// <see cref="RefreshDatabaseFileSizeAsync"/> no-ops on non-READY
    /// transitions, so a single observer covers both the unlock and the
    /// lock paths (the lock path zeros DatabaseFileSize via the guard).
    /// </summary>
    private async Task OnDbStateChangedAsync(CancellationToken cancellationToken)
    {
        _ = DbState.State;
        await RefreshDatabaseFileSizeAsync(cancellationToken);
    }

    private string FormatOperationError(Exception ex) => ex switch
    {
        // Disk-locked race during user-driven Lock — typed exception from the
        // bridge gate. Localized user-facing copy lives in this consumer's
        // resx; the exception's diagnostic message is for developer logs only.
        PoolLockedException => Localizer["Error_PoolLocked"],
        _ => Localizer["Error_Operation", ex.Message],
    };

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