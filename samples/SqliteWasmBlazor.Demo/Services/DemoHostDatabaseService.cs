using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.UI.Services;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.Demo.Services;

/// <summary>
/// Demo-side <see cref="IHostDatabaseService"/> — the single
/// "reset everything" entry point for the Demo. Three callsites route
/// through here (Crypto.UI's <c>DatabaseErrorAlert</c> for boot-failure
/// recovery, the encryption page's Reset button via
/// <c>EncryptionModel.Reset</c>, and the Administration page's
/// <c>ResetDatabaseAsync</c>). All three want the same end state: disk
/// back to Plain, user signed out, every consumer DbContext re-migrated,
/// boot status promoted to READY.
///
/// <para>
/// Sequence — minimal manual orchestration; the auth signout falls out
/// of the existing reactive cascade so this service stays focused on
/// what only the host knows (its DbContexts):
/// <list type="number">
///   <item><see cref="IEncryptedSqliteWasmDatabaseService.ResetDiskAsync"/> —
///         wipes every DB file from OPFS, drops the worker globalKey,
///         clears the PRF cache. The cache clear emits
///         <c>IPrfService.KeyExpired</c>, which
///         <c>AuthenticationModel.OnSessionExpiredAsync</c> consumes;
///         the handler reads the now-empty manifest and full-signs-out
///         (PublicKey=null + CredentialId=null) without manual wiring
///         here.</item>
///   <item><c>MigrateAsync</c> per registered DbContext — re-creates
///         the schema on the now-empty Plain disk. This is the only
///         step that requires host-specific knowledge.</item>
///   <item><see cref="IDbInitializationReporter.Report"/>(<see cref="DbInitState.READY"/>) —
///         dismisses any boot-failure alert.</item>
/// </list>
/// </para>
///
/// <para>
/// Adding a new DbContext to the Demo: inject the new factory and add
/// one <c>MigrateAsync</c> call here. All three Reset callsites pick
/// it up automatically — no per-callsite enumeration.
/// </para>
/// </summary>
public sealed class DemoHostDatabaseService : IHostDatabaseService
{
    private readonly IEncryptedSqliteWasmDatabaseService _session;
    private readonly IDbContextFactory<TodoDbContext> _todoFactory;
    private readonly IDbContextFactory<NoteDbContext> _noteFactory;
    private readonly IDbInitializationReporter _reporter;

    public DemoHostDatabaseService(
        IEncryptedSqliteWasmDatabaseService session,
        IDbContextFactory<TodoDbContext> todoFactory,
        IDbContextFactory<NoteDbContext> noteFactory,
        IDbInitializationReporter reporter)
    {
        _session = session;
        _todoFactory = todoFactory;
        _noteFactory = noteFactory;
        _reporter = reporter;
    }

    public bool IsAvailable => true;

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        // Scorched-earth disk wipe. PRF cache clear cascades through
        // KeyExpired → AuthenticationModel.OnSessionExpiredAsync, which
        // reads the now-empty manifest and full-signs-out (clears both
        // PublicKey AND CredentialId). No manual Auth.SignOut here.
        await _session.ResetDiskAsync(cancellationToken);

        // Re-migrate every consumer DbContext. The host-specific step.
        await using (var todoCtx = await _todoFactory.CreateDbContextAsync(cancellationToken))
        {
            await todoCtx.Database.MigrateAsync(cancellationToken);
        }

        await using (var noteCtx = await _noteFactory.CreateDbContextAsync(cancellationToken))
        {
            await noteCtx.Database.MigrateAsync(cancellationToken);
        }

        // Clear any lingering boot-failure alert.
        _reporter.Report(DbInitState.READY);
    }

    public async ValueTask<bool> HasAnyDataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using (var todo = await _todoFactory.CreateDbContextAsync(cancellationToken))
            {
                if (await todo.TodoItems.AnyAsync(cancellationToken))
                {
                    return true;
                }
            }
            await using (var note = await _noteFactory.CreateDbContextAsync(cancellationToken))
            {
                if (await note.Notes.AnyAsync(cancellationToken))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            // Fail-open: probe errors (transient DB unavailability, race
            // with a Reset, etc.) shouldn't suppress an otherwise-valid
            // export affordance.
            return true;
        }
    }
}
