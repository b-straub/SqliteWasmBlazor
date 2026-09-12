using System.Data.Common;
using System.Diagnostics;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Cancellation;

/// <summary>
/// One statement that holds the worker for over a second: a recursive CTE
/// counted in a single step. No table, nothing written, nothing to clean up
/// — and, like the FTS sort that motivated cancellation, one
/// <c>sqlite3_step</c> that only a progress handler can get out of.
///
/// <para>
/// Ten million rows take ~1.4 s in headless Chromium on a dev box. The two
/// cases tell the modes apart by whether the statement issued next is
/// answered in tens of milliseconds or only after this one is done, so the
/// gap between those has to stay wide: shortening the CTE to where an
/// un-interrupted statement finishes inside the probe bound would let a
/// broken interrupt pass.
/// </para>
/// </summary>
internal static class LongRunningStatement
{
    public const string Sql =
        "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 10000000) " +
        "SELECT COUNT(*) FROM n";

    /// <summary>
    /// The statement issued after the cancel is answered within this when the
    /// worker was interrupted (measured: ~50 ms, one poll interval plus the
    /// probe itself), and only after the whole statement when it was not.
    /// </summary>
    public const int InterruptedProbeBoundMs = 300;

    /// <summary>
    /// The same probe waits at least this long behind an abandoned statement
    /// the worker had to finish. Well under the statement's ~1.4 s so a slow
    /// runner still passes, well over <see cref="InterruptedProbeBoundMs"/>
    /// so the two modes cannot be confused.
    /// </summary>
    public const int AbandonedProbeFloorMs = 500;

    /// <summary>
    /// Runs <see cref="Sql"/> on <paramref name="connection"/>, cancels the
    /// token after <paramref name="cancelAfterMs"/>, and returns how long the
    /// call took from start to <see cref="OperationCanceledException"/>. A
    /// statement that completes instead is a failure: it did not run long
    /// enough to prove anything.
    /// </summary>
    public static async Task<long> RunUntilCancelledAsync(DbConnection connection, int cancelAfterMs)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        using var cts = new CancellationTokenSource(cancelAfterMs);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var count = await command.ExecuteScalarAsync(cts.Token);
            throw new InvalidOperationException(
                $"The statement completed (COUNT = {count}) in {stopwatch.ElapsedMilliseconds} ms " +
                $"instead of being cancelled after {cancelAfterMs} ms; it is too short to test with.");
        }
        catch (OperationCanceledException)
        {
            return stopwatch.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// A trivial statement, timed. Whether it is answered at once or only
    /// after the worker has finished an abandoned predecessor is the whole
    /// difference between the two cancellation modes.
    /// </summary>
    public static async Task<long> ProbeAsync(DbConnection connection)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1";

        var stopwatch = Stopwatch.StartNew();
        await probe.ExecuteScalarAsync();
        return stopwatch.ElapsedMilliseconds;
    }
}
