using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Demo.Models;

/// <summary>
/// Drives the <c>MultiDatabase.razor</c> page. Owns the two short-lived
/// EF dataset projections (Todos / Notes), the new-todo / new-note form
/// state, and the default <see cref="TodoList"/> id used when seeding
/// the first run.
///
/// <para>
/// <b>Disk-state self-guard.</b> Every EF call funnels through
/// <see cref="LoadDataAsync"/> / the per-command writes; each one checks
/// <c>DbState.State == READY</c> before opening a context, so a refresh
/// while the encrypted disk is locked no longer blows up with the
/// bridge's <c>DiskLockedException</c> gate. The auto-detected internal
/// observer <see cref="OnDbStateChangedAsync"/> reloads on every state
/// transition (boot probe, lifecycle Unlock, Lock, Reset) — so when the
/// user signs in and the lifecycle auto-unlocks the disk, the page
/// hydrates without needing AuthorizeView to remount any subtree. Mirror
/// of the pattern in <see cref="TodoListModel"/>.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class MultiDatabaseModel : ObservableModel
{
    public partial MultiDatabaseModel(
        IDbContextFactory<TodoDbContext> todoFactory,
        IDbContextFactory<NoteDbContext> noteFactory,
        DbStateModel dbState,
        StatusModel statusModel,
        IStringLocalizer<MultiDatabaseModel> localizer);

    public partial IReadOnlyList<Todo> Todos { get; set; } = [];
    public partial IReadOnlyList<Note> Notes { get; set; } = [];

    public partial string NewTodoTitle { get; set; } = string.Empty;
    public partial string NewNoteTitle { get; set; } = string.Empty;
    public partial string NewNoteContent { get; set; } = string.Empty;
    public partial string NewNoteTag { get; set; } = string.Empty;
    public partial Guid? NewNoteTodoId { get; set; }

    private Guid _defaultListId;

    [ObservableCommand(nameof(AddTodoAsync), nameof(CanAddTodo), nameof(FormatOperationError))]
    public partial IObservableCommandAsync AddTodo { get; }

    [ObservableCommand(nameof(ToggleTodoAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync<Todo> ToggleTodo { get; }

    [ObservableCommand(nameof(DeleteTodoAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync<Todo> DeleteTodo { get; }

    [ObservableCommand(nameof(AddNoteAsync), nameof(CanAddNote), nameof(FormatOperationError))]
    public partial IObservableCommandAsync AddNote { get; }

    [ObservableCommand(nameof(DeleteNoteAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync<Note> DeleteNote { get; }

    private bool CanAddTodo() => !string.IsNullOrWhiteSpace(NewTodoTitle);

    private bool CanAddNote() =>
        !string.IsNullOrWhiteSpace(NewNoteTitle) && !string.IsNullOrWhiteSpace(NewNoteContent);

    public int NoteCount(Guid todoId) => Notes.Count(n => n.TodoId == todoId);

    public Todo? FindTodo(Guid? todoId) =>
        todoId is { } id ? Todos.FirstOrDefault(t => t.Id == id) : null;

    /// <summary>
    /// Initial dataset hydration — covers the "DbState already READY at
    /// construction" branch (user navigates to /multidatabase after
    /// unlocking elsewhere). Transitions into READY are handled by the
    /// auto-detected observer below.
    /// </summary>
    protected override async Task OnContextReadyAsync(CancellationToken cancellationToken)
    {
        await LoadDataAsync(cancellationToken);
    }

    /// <summary>
    /// Auto-detected internal observer (RxBlazorV2 §7) — keyed on
    /// <c>DbState.State</c>. Fires on every disk state transition so the
    /// page hydrates whenever the encrypted VFS becomes available, and
    /// clears its dataset on Lock / Reset. The self-guard inside
    /// <see cref="LoadDataAsync"/> takes care of the non-READY branch.
    /// </summary>
    private async Task OnDbStateChangedAsync(CancellationToken cancellationToken)
    {
        _ = DbState.State;
        await LoadDataAsync(cancellationToken);
    }

    private async Task LoadDataAsync(CancellationToken cancellationToken)
    {
        if (DbState.State != DbInitState.READY)
        {
            Todos = [];
            Notes = [];
            return;
        }

        await EnsureDefaultListAsync(cancellationToken);

        await using var todoContext = await TodoFactory.CreateDbContextAsync(cancellationToken);
        Todos = await todoContext.Todos
            .OrderByDescending(t => t.Priority)
            .ToListAsync(cancellationToken);

        await using var noteContext = await NoteFactory.CreateDbContextAsync(cancellationToken);
        Notes = await noteContext.Notes
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureDefaultListAsync(CancellationToken cancellationToken)
    {
        if (_defaultListId != Guid.Empty)
        {
            return;
        }

        await using var todoContext = await TodoFactory.CreateDbContextAsync(cancellationToken);
        var list = await todoContext.TodoLists.FirstOrDefaultAsync(cancellationToken);
        if (list is null)
        {
            list = new TodoList
            {
                Id = Guid.NewGuid(),
                Title = "Multi-DB Demo",
                CreatedAt = DateTime.UtcNow,
            };
            todoContext.TodoLists.Add(list);
            await todoContext.SaveChangesAsync(cancellationToken);
        }
        _defaultListId = list.Id;
    }

    private async Task AddTodoAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultListAsync(cancellationToken);

        await using (var ctx = await TodoFactory.CreateDbContextAsync(cancellationToken))
        {
            ctx.Todos.Add(new Todo
            {
                Id = Guid.NewGuid(),
                Title = NewTodoTitle,
                TodoListId = _defaultListId,
            });
            await ctx.SaveChangesAsync(cancellationToken);
        }

        NewTodoTitle = string.Empty;
        await LoadDataAsync(cancellationToken);
    }

    private async Task ToggleTodoAsync(Todo todo, CancellationToken cancellationToken)
    {
        await using (var ctx = await TodoFactory.CreateDbContextAsync(cancellationToken))
        {
            var entity = await ctx.Todos.FindAsync([todo.Id], cancellationToken);
            if (entity is not null)
            {
                entity.Completed = !entity.Completed;
                entity.CompletedAt = entity.Completed ? DateTime.UtcNow : null;
                await ctx.SaveChangesAsync(cancellationToken);
            }
        }
        await LoadDataAsync(cancellationToken);
    }

    private async Task DeleteTodoAsync(Todo todo, CancellationToken cancellationToken)
    {
        await using (var ctx = await TodoFactory.CreateDbContextAsync(cancellationToken))
        {
            var entity = await ctx.Todos.FindAsync([todo.Id], cancellationToken);
            if (entity is not null)
            {
                ctx.Todos.Remove(entity);
                await ctx.SaveChangesAsync(cancellationToken);
            }
        }
        await LoadDataAsync(cancellationToken);
    }

    private async Task AddNoteAsync(CancellationToken cancellationToken)
    {
        await using (var ctx = await NoteFactory.CreateDbContextAsync(cancellationToken))
        {
            ctx.Notes.Add(new Note
            {
                Id = Guid.NewGuid(),
                Title = NewNoteTitle,
                Content = NewNoteContent,
                Tag = string.IsNullOrWhiteSpace(NewNoteTag) ? null : NewNoteTag,
                TodoId = NewNoteTodoId,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync(cancellationToken);
        }

        NewNoteTitle = string.Empty;
        NewNoteContent = string.Empty;
        NewNoteTag = string.Empty;
        NewNoteTodoId = null;
        await LoadDataAsync(cancellationToken);
    }

    private async Task DeleteNoteAsync(Note note, CancellationToken cancellationToken)
    {
        await using (var ctx = await NoteFactory.CreateDbContextAsync(cancellationToken))
        {
            var entity = await ctx.Notes.FindAsync([note.Id], cancellationToken);
            if (entity is not null)
            {
                ctx.Notes.Remove(entity);
                await ctx.SaveChangesAsync(cancellationToken);
            }
        }
        await LoadDataAsync(cancellationToken);
    }

    private string FormatOperationError(Exception ex) => ex switch
    {
        DiskLockedException => Localizer["Error_DiskLocked"],
        OperationCanceledException => Localizer["Error_Cancelled"],
        _ => Localizer["Error_Operation", ex.Message],
    };
}
