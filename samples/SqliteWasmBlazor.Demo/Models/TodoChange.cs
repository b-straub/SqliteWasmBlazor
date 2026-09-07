namespace SqliteWasmBlazor.Demo.Models;

/// <summary>
/// What a completed mutation did to the todo set.
/// </summary>
public enum TodoChangeKind
{
    ADDED,
    TOGGLED,
    DELETED,
    REFRESHED,
}

/// <summary>
/// The last completed mutation, published by the command that performed it and
/// observed by <see cref="TodoListModel"/>'s listing batch.
///
/// <para>
/// A record, so the generated setter is equality-guarded; <paramref name="At"/>
/// is what makes two of the same operation two distinct values rather than a
/// silent no-op.
/// </para>
///
/// <para>
/// <b>It lives in its own file for a reason.</b> MSBuild names an embedded
/// <c>.resx</c> after the first class, record or struct declared in the
/// same-named <c>.cs</c> file (the DependentUpon convention — enums are
/// skipped, records are not). Declaring this record above
/// <see cref="TodoListModel"/> in <c>TodoListModel.cs</c> renamed
/// <c>TodoListModel.resx</c> to <c>...TodoChange.resources</c>, so
/// <c>IStringLocalizer&lt;TodoListModel&gt;</c> found no resource set and
/// every lookup fell back to echoing its key. The build stays green — the
/// page just renders <c>Page_Title</c> instead of a title.
/// </para>
/// </summary>
public sealed record TodoChange(TodoChangeKind Kind, DateTime At);
