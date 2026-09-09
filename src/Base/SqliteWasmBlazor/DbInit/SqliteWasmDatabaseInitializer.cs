using Microsoft.AspNetCore.Components;

namespace SqliteWasmBlazor;

/// <summary>
/// Runs database initialization once the app has rendered. Place it once, in
/// the layout:
/// <code>&lt;SqliteWasmDatabaseInitializer /&gt;</code>
/// </summary>
/// <remarks>
/// <para>
/// It renders nothing. Its whole job is to be a point in time — the first one
/// at which there is a UI. Initializing from <c>Program.cs</c> happens before
/// that, which is why an encrypted pool could never be opened there and why a
/// migration over a large database showed as a frozen blank page.
/// </para>
/// <para>
/// Every host needs it, including ADO-only ones: starting the worker is part of
/// what it does. A host with no contexts declared simply has nothing to migrate.
/// </para>
/// <para>
/// Safe on an encrypted pool that is still locked: the initializer checks
/// whether the database can be opened and leaves the schema work owed until
/// <c>UnlockAsync</c>.
/// </para>
/// <para>
/// Blazor invokes <c>OnAfterRenderAsync</c> child-before-parent, so a routed
/// page can run before the layout hosting this. Code in that position should
/// await <see cref="ISqliteWasmInitializer.InitializeAsync"/> itself — it is
/// idempotent.
/// </para>
/// </remarks>
public sealed class SqliteWasmDatabaseInitializer : ComponentBase
{
    /// <summary>The initialization this component drives.</summary>
    [Inject]
    public required ISqliteWasmInitializer Initializer { get; init; }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Initializer.InitializeAsync();
        }
    }
}
