// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Default backing store for <see cref="IDbInitializationStatus"/> and
/// <see cref="IDbInitializationReporter"/>. Registered as a singleton by
/// <c>AddSqliteWasm</c> under both interfaces; replaced by
/// <c>DbStateModel</c> when the host calls <c>AddCryptoUI()</c> (which
/// promotes the seam to a reactive <c>ObservableModel</c>).
/// </summary>
public sealed class DbInitializationService : IDbInitializationStatus, IDbInitializationReporter
{
    /// <inheritdoc />
    public DbInitState State { get; private set; } = DbInitState.NOT_STARTED;

    /// <inheritdoc />
    public IDbInitFailure? Failure { get; private set; }

    /// <inheritdoc />
    public void Report(DbInitState state, IDbInitFailure? failure = null)
    {
        if (state == State && ReferenceEquals(failure, Failure))
        {
            return;
        }

        State = state;
        Failure = failure;
    }
}
