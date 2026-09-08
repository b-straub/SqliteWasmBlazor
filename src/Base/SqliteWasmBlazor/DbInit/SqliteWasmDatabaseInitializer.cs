using Microsoft.AspNetCore.Components;

namespace SqliteWasmBlazor;

/// <summary>
/// Drives the schema work registered by <c>InitializeSqliteWasmDatabaseAsync</c>
/// once the app has rendered. Place it once, in the layout:
/// <code>&lt;SqliteWasmDatabaseInitializer /&gt;</code>
/// </summary>
/// <remarks>
/// <para>
/// It renders nothing. Its whole job is to be a point in time — the first one
/// at which a migration can be reported to somebody. Applying migrations from
/// <c>Program.cs</c> means they run before there is a UI, so a database large
/// enough for the work to be noticeable freezes a blank page instead.
/// </para>
/// <para>
/// A host that never calls <c>InitializeSqliteWasmDatabaseAsync&lt;TContext&gt;</c>
/// — the ADO-only ones, which use <c>InitializeSqliteWasmAsync</c> — has no
/// steps registered and does not need this component.
/// </para>
/// <para>
/// Safe on an encrypted pool that is still locked: the initializer checks
/// whether the database can be opened and leaves the work owed until
/// <c>UnlockAsync</c>.
/// </para>
/// </remarks>
public sealed class SqliteWasmDatabaseInitializer : ComponentBase
{
    /// <summary>The schema work registered during boot.</summary>
    [Inject]
    public required IDbSchemaInitializer SchemaInitializer { get; init; }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await SchemaInitializer.EnsureSchemaAsync();
        }
    }
}
