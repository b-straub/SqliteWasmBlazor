// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Write side of the boot status surface. Library boot helpers
/// (<c>InitializeSqliteWasmAsync</c>, <c>InitializeSqliteWasmDatabaseAsync</c>)
/// report through this; downstream packages and apps composing additional
/// boot stages may also report their own <see cref="IDbInitFailure"/>s.
///
/// <para>
/// Reporters and statuses are backed by the same singleton — resolving both
/// from DI returns two facets of the same instance.
/// </para>
/// </summary>
public interface IDbInitializationReporter
{
    /// <summary>
    /// Promote the lifecycle to <paramref name="state"/>. Pass
    /// <paramref name="failure"/> for any non-READY terminal state.
    /// </summary>
    void Report(DbInitState state, IDbInitFailure? failure = null);
}
