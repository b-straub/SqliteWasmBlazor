using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Cancellation;

/// <summary>
/// With a service worker controlling the page, cancelling the token stops
/// the statement the worker is running — not just the wait for it. The
/// proof is the statement issued right after: it is answered at once,
/// because the worker was freed, not merely abandoned.
///
/// <para>
/// Runs on the crypto-plane run, which registers a claiming service worker
/// before Blazor starts (see <c>test-boot.js</c>). On the plain-plane run
/// there is no service worker and this case is skipped;
/// <see cref="CancellationUnavailableWithoutServiceWorkerTest"/> covers that
/// run. Which run it is comes from <see cref="TestPlane"/>, not from
/// <see cref="ISqliteWasmDatabaseService.CanCancelQueries"/>, so a service
/// worker that failed to take control fails the case instead of skipping it.
/// </para>
/// </summary>
internal class CancellationInterruptsRunningStatementTest(
    IDbContextFactory<TodoDbContext> factory,
    ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "Cancellation_InterruptsRunningStatement";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (TestPlane.IsPlain)
        {
            return "SKIPPED";
        }

        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService was not supplied");
        }

        if (!DatabaseService.CanCancelQueries)
        {
            throw new InvalidOperationException(
                "The crypto-plane run registers a service worker that claims the page before Blazor " +
                "starts, yet CanCancelQueries is false — the bridge saw no controller.");
        }

        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        var cancelledAfter = await LongRunningStatement.RunUntilCancelledAsync(connection, cancelAfterMs: 100);
        if (cancelledAfter > 300)
        {
            throw new InvalidOperationException(
                $"OperationCanceledException arrived {cancelledAfter} ms after the statement started; " +
                "expected within 300 ms of a cancel at 100 ms.");
        }

        var probeMs = await LongRunningStatement.ProbeAsync(connection);
        if (probeMs > LongRunningStatement.InterruptedProbeBoundMs)
        {
            throw new InvalidOperationException(
                $"The statement issued after the cancel took {probeMs} ms: the worker was still running " +
                "the cancelled statement, so the interrupt never reached it.");
        }

        Console.WriteLine($"[{Name}] cancelled after {cancelledAfter} ms; next statement answered in {probeMs} ms");
        return "OK";
    }
}
