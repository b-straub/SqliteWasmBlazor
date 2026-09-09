namespace SqliteWasmBlazor.TestApp.TestInfrastructure;

/// <summary>
/// Records what initialization reported, so a test can assert on the
/// transitions rather than only the state left behind at the end.
/// </summary>
/// <remarks>
/// <see cref="DbInitState.MIGRATING"/> is the reason this exists: it is
/// transient by design — gone again by the time anything reads
/// <c>IDbInitializationStatus</c> — so "was it announced when nothing was
/// pending?" is not a question the status surface can answer.
/// </remarks>
internal sealed class RecordingDbInitNotifier : IDbInitNotifier
{
    private readonly List<DbInitState> _states = [];

    public IReadOnlyList<DbInitState> States
    {
        get
        {
            lock (_states)
            {
                return [.. _states];
            }
        }
    }

    public void Clear()
    {
        lock (_states)
        {
            _states.Clear();
        }
    }

    public ValueTask NotifyAsync(
        DbInitNotification notification,
        CancellationToken cancellationToken = default)
    {
        lock (_states)
        {
            _states.Add(notification.State);
        }

        return ValueTask.CompletedTask;
    }
}
