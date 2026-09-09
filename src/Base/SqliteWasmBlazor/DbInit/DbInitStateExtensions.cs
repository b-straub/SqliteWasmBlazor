namespace SqliteWasmBlazor;

/// <summary>
/// Reading <see cref="DbInitState"/> as a capability rather than a label.
/// </summary>
public static class DbInitStateExtensions
{
    /// <summary>
    /// Whether the worker is up, so a database or pool operation can be sent
    /// to it.
    /// </summary>
    /// <param name="state">The current boot state.</param>
    /// <remarks>
    /// <para>
    /// This is the guard a UI model needs, and it is deliberately weaker than
    /// "ready to query". <see cref="DbInitState.ENCRYPTED_LOCKED"/> and the
    /// schema failures all mean the worker started and will answer — a panel
    /// asking the pool whether it is encrypted is exactly what should happen in
    /// those states. Only <see cref="DbInitState.NOT_STARTED"/> and
    /// <see cref="DbInitState.INITIALIZING"/> mean there is nothing to talk to
    /// yet, and <see cref="DbInitState.TAB_LOCKED"/> means there never will be.
    /// </para>
    /// <para>
    /// Gating queries is a different question, answered by
    /// <see cref="DbInitState.READY"/> — or, for a component, by
    /// <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c>.
    /// </para>
    /// </remarks>
    public static bool IsWorkerAvailable(this DbInitState state) =>
        state is not (DbInitState.NOT_STARTED
                   or DbInitState.INITIALIZING
                   or DbInitState.TAB_LOCKED);
}
