using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Cancellation;

/// <summary>
/// Without a service worker, cancellation is what it always was: the wait
/// ends with <see cref="OperationCanceledException"/> at once, the worker
/// finishes the statement on its own, and the next statement waits behind
/// it. <see cref="ISqliteWasmDatabaseService.CanCancelQueries"/> says so.
///
/// <para>
/// Runs on the plain-plane run, which registers no service worker; skipped
/// on the crypto-plane run, where
/// <see cref="CancellationInterruptsRunningStatementTest"/> covers the other
/// mode. The wait for the abandoned statement is part of the case — the
/// suite would queue behind it anyway.
/// </para>
/// </summary>
internal class CancellationUnavailableWithoutServiceWorkerTest(
    IDbContextFactory<TodoDbContext> factory,
    ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "Cancellation_UnavailableWithoutServiceWorker";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (!TestPlane.IsPlain)
        {
            return "SKIPPED";
        }

        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService was not supplied");
        }

        if (DatabaseService.CanCancelQueries)
        {
            throw new InvalidOperationException(
                "The plain-plane run registers no service worker, yet CanCancelQueries is true.");
        }

        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        var cancelledAfter = await LongRunningStatement.RunUntilCancelledAsync(connection, cancelAfterMs: 100);
        if (cancelledAfter > 300)
        {
            throw new InvalidOperationException(
                $"OperationCanceledException arrived {cancelledAfter} ms after the statement started; " +
                "without a service worker the wait must still end at the cancel, not when the worker is done.");
        }

        // The degraded mode, stated as a fact: the worker is still running the
        // abandoned statement, so the probe waits behind it. A probe answered
        // at once would mean the statement was too short to show that.
        var probeMs = await LongRunningStatement.ProbeAsync(connection);
        if (probeMs < LongRunningStatement.AbandonedProbeFloorMs)
        {
            throw new InvalidOperationException(
                $"The statement issued after the cancel was answered in {probeMs} ms, but with no service " +
                "worker it should have waited for the abandoned statement to finish.");
        }

        Console.WriteLine($"[{Name}] cancelled after {cancelledAfter} ms; next statement waited {probeMs} ms behind the abandoned one");
        return "OK";
    }
}
