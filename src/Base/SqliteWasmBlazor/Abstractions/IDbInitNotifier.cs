namespace SqliteWasmBlazor;

/// <summary>
/// What initialization just did, as data.
/// </summary>
/// <param name="State">The state being entered.</param>
/// <param name="DatabaseName">
/// The database the state is about, when it is about one — the context being
/// migrated, or the one a failure came from. <c>null</c> for app-wide states.
/// </param>
/// <param name="Failure">The diagnosis, when <paramref name="State"/> is a failure.</param>
public readonly record struct DbInitNotification(
    DbInitState State,
    string? DatabaseName,
    IDbInitFailure? Failure);

/// <summary>
/// A host-supplied sink for initialization progress, so an app can tell its
/// user what is happening while a migration runs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately carries no text. This package has no localization and no UI
/// framework, so wording it here would either ship English into every app or
/// drag a UI dependency into the base package. The host receives the facts and
/// writes the sentence — in the Demo, onto <c>StatusModel</c> with its own
/// resx.
/// </para>
/// <para>
/// Distinct from <see cref="IDbInitializationStatus"/>, which answers "can I
/// query yet" and is what the <c>DatabaseOpen</c> policy gates on. This is a
/// push channel for things worth saying once; that is a pull surface for the
/// current truth. Hosts that need neither register nothing and get
/// <see cref="NullDbInitNotifier"/>.
/// </para>
/// </remarks>
public interface IDbInitNotifier
{
    /// <summary>Called on every state transition initialization reports.</summary>
    /// <param name="notification">What happened.</param>
    /// <param name="cancellationToken">Cancels the notification.</param>
    /// <remarks>
    /// Awaited by the initializer, so a slow implementation slows boot. It must
    /// not throw: an exception here would fail an initialization that otherwise
    /// succeeded.
    /// </remarks>
    ValueTask NotifyAsync(DbInitNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// The do-nothing sink used when a host registers none.
/// </summary>
public sealed class NullDbInitNotifier : IDbInitNotifier
{
    /// <summary>The shared instance.</summary>
    public static NullDbInitNotifier Instance { get; } = new();

    private NullDbInitNotifier()
    {
    }

    /// <inheritdoc />
    public ValueTask NotifyAsync(
        DbInitNotification notification,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
