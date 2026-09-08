namespace SqliteWasmBlazor;

/// <summary>
/// Applies pending EF Core migrations once the database is actually openable.
/// </summary>
/// <remarks>
/// <para>
/// Migrations used to run inside <c>InitializeSqliteWasmDatabaseAsync</c>, in
/// <c>Program.cs</c>, before the app rendered. That placement is defined by
/// when the host happens to call it rather than by when the database can be
/// opened, and the two only coincide for a plain pool. An encrypted pool is
/// locked at that moment and cannot be, because its key comes from a WebAuthn
/// ceremony that needs a UI — so the migration step was simply skipped, and
/// nothing ever came back for it.
/// </para>
/// <para>
/// Both pool kinds now reach the same step here: immediately for a plain pool,
/// after <c>UnlockAsync</c> for an encrypted one. Because it runs once a UI
/// exists, it can report <see cref="DbInitState.MIGRATING"/> — which matters,
/// since a migration over a large encrypted database takes seconds.
/// </para>
/// </remarks>
public interface IDbSchemaInitializer
{
    /// <summary>
    /// Applies pending migrations for every context registered through
    /// <c>InitializeSqliteWasmDatabaseAsync</c>, then reports
    /// <see cref="DbInitState.READY"/> — or the failure that stopped it.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call from more than one trigger: the work runs
    /// once, and later calls return as soon as it has.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the schema work.</param>
    ValueTask EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets that the work ran, so the next <see cref="EnsureSchemaAsync"/>
    /// does it again. For the paths that replace the database underneath the
    /// app — a whole-pool import, a reset — after which the schema on disk is
    /// no longer the one that was migrated.
    /// </summary>
    void Reset();
}
