namespace SqliteWasmBlazor;

/// <summary>
/// Starts the worker and brings every registered database to its current
/// schema. Driven by <c>&lt;SqliteWasmDatabaseInitializer/&gt;</c> after the
/// first render.
/// </summary>
/// <remarks>
/// <para>
/// None of this happens in <c>Program.cs</c>, and that is the point. An
/// encrypted pool is locked at every boot — its key comes from a WebAuthn
/// ceremony that cannot run before the app renders — so initialization that
/// completes before there is a UI can never open one. Running after the first
/// render is also what makes a migration reportable: over a populated database
/// it takes seconds, and from <c>Program.cs</c> those seconds are a blank page.
/// </para>
/// <para>
/// Declare contexts with <c>AddSqliteWasmDbContext&lt;TContext&gt;</c>; they
/// are migrated in declaration order, stopping at the first failure.
/// </para>
/// </remarks>
public interface ISqliteWasmInitializer
{
    /// <summary>
    /// Starts the worker, checks whether the pool can be opened, and applies
    /// pending migrations. Reports the outcome to
    /// <see cref="IDbInitializationReporter"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels initialization.</param>
    /// <remarks>
    /// Idempotent and awaitable, so any code path needing the database can call
    /// it — a page whose <c>OnAfterRenderAsync</c> runs before the layout's
    /// component, a test driving boot directly. Later calls re-report the
    /// outcome the first produced rather than redoing the work.
    /// </remarks>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies pending migrations only, assuming the worker is already up.
    /// </summary>
    /// <param name="cancellationToken">Cancels the schema work.</param>
    /// <remarks>
    /// For the moment an encrypted pool becomes openable:
    /// <c>UnlockAsync</c> calls this, because boot could not migrate ciphertext
    /// and left the work owed.
    /// </remarks>
    ValueTask EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the outcome so the next call does the work again.
    /// </summary>
    /// <remarks>
    /// For paths that replace the database underneath the app — a whole-pool
    /// import, a reset — and for tests re-driving boot. Calling it is a claim
    /// that what is on disk is no longer what was initialized.
    /// </remarks>
    void Reset();
}
