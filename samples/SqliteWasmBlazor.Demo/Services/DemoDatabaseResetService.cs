using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.UI.Services;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.Demo.Services;

/// <summary>
/// Demo-side <see cref="IDatabaseResetService"/> consumed by Crypto.UI's
/// <c>DatabaseErrorAlert</c>. Deletes both demo databases (TodoDb / NotesDb),
/// re-runs migrations, and promotes the boot status back to
/// <see cref="DbInitState.READY"/> so the alert auto-clears.
///
/// <para>
/// Lifted from the previous local <c>Components/DatabaseErrorAlert.razor</c>
/// (which mixed UI + reset logic) — the Crypto.UI panel now owns the UI
/// surface, this service owns the host-specific recovery action. Routing
/// success / failure status text is handled by the panel's command formatter
/// via the shared <c>StatusModel</c>; this method just throws on failure
/// and lets the formatter render <c>Error_Reset</c>.
/// </para>
/// </summary>
public sealed class DemoDatabaseResetService : IDatabaseResetService
{
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IDbContextFactory<TodoDbContext> _todoFactory;
    private readonly IDbContextFactory<NoteDbContext> _noteFactory;
    private readonly IDbInitializationReporter _reporter;

    public DemoDatabaseResetService(
        ISqliteWasmDatabaseService databaseService,
        IDbContextFactory<TodoDbContext> todoFactory,
        IDbContextFactory<NoteDbContext> noteFactory,
        IDbInitializationReporter reporter)
    {
        _databaseService = databaseService;
        _todoFactory = todoFactory;
        _noteFactory = noteFactory;
        _reporter = reporter;
    }

    public bool IsAvailable => true;

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        await _databaseService.DeleteDatabaseAsync("TodoDb.db");
        await _databaseService.DeleteDatabaseAsync("NotesDb.db");

        await using (var todoCtx = await _todoFactory.CreateDbContextAsync(cancellationToken))
        {
            await todoCtx.Database.MigrateAsync(cancellationToken);
        }

        await using (var noteCtx = await _noteFactory.CreateDbContextAsync(cancellationToken))
        {
            await noteCtx.Database.MigrateAsync(cancellationToken);
        }

        _reporter.Report(DbInitState.READY);
    }
}
